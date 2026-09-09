using System.Text;
using ClashTray.Contracts;

namespace ClashTray.Core;

public sealed class RuntimeConfigBuilder
{
    private const string UnitedStatesCommonGroupName = "美国常用";
    private const string UnitedStatesProxyFilter = "(?i)(?:^|[^A-Za-z])(?:US|USA)(?:[^A-Za-z]|$)|美国|United[ _-]?States";
    private readonly ControllerSecretStore _secretStore;

    public RuntimeConfigBuilder(ControllerSecretStore secretStore)
    {
        _secretStore = secretStore;
    }

    public async Task<string> BuildAsync(
        string sourcePath,
        string destinationPath,
        AppSettings settings,
        CancellationToken cancellationToken = default)
    {
        SettingsValidator.Validate(settings);
        var source = await File.ReadAllLinesAsync(sourcePath, cancellationToken);
        var withUnitedStatesGroupOverride = ApplyUnitedStatesGroupOverride(source);
        var withTunOverride = ApplyTunOverride(withUnitedStatesGroupOverride, settings.TunEnabled);
        var filtered = withTunOverride.Where(line => !IsManagedLine(line)).ToList();
        filtered.Add(string.Empty);
        filtered.Add($"external-controller: 127.0.0.1:{settings.ControllerPort}");
        filtered.Add($"secret: {_secretStore.GetOrCreate()}");
        filtered.Add($"allow-lan: {(settings.AllowLan ? "true" : "false")}");
        filtered.Add($"ipv6: {(settings.Ipv6 ? "true" : "false")}");
        filtered.Add($"tcp-concurrent: {(settings.TcpConcurrent ? "true" : "false")}");
        filtered.Add($"log-level: {settings.LogLevel.Trim().ToLowerInvariant()}");
        filtered.Add($"port: {settings.HttpPort}");
        filtered.Add($"mixed-port: {settings.MixedPort}");
        filtered.Add($"socks-port: {settings.SocksPort}");

        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        var tempPath = $"{destinationPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllLinesAsync(tempPath, filtered, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), cancellationToken);
            File.Move(tempPath, destinationPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }

        WindowsPathSecurity.ProtectRuntimeFile(destinationPath);
        return destinationPath;
    }

    private static string[] ApplyUnitedStatesGroupOverride(string[] source)
    {
        var proxyGroupsIndex = -1;
        var proxyGroupsIndent = -1;
        for (var index = 0; index < source.Length; index++)
        {
            if (TryGetRootKey(source[index], out var key)
                && key.Equals("proxy-groups", StringComparison.OrdinalIgnoreCase))
            {
                proxyGroupsIndex = index;
                proxyGroupsIndent = GetIndent(source[index]);
                break;
            }
        }

        if (proxyGroupsIndex < 0)
        {
            return source.ToArray();
        }

        var groupIndent = -1;
        var groupStart = -1;
        var groups = new List<(int Start, int End)>();
        for (var index = proxyGroupsIndex + 1; index <= source.Length; index++)
        {
            if (index == source.Length
                || IsRootBoundary(source[index], proxyGroupsIndent))
            {
                if (groupStart >= 0)
                {
                    groups.Add((groupStart, index));
                }

                break;
            }

            var trimmed = source[index].TrimStart();
            if (trimmed.Length == 0 || trimmed.StartsWith('#'))
            {
                continue;
            }

            var indent = GetIndent(source[index]);
            if (groupIndent < 0
                && indent > proxyGroupsIndent
                && trimmed.StartsWith('-'))
            {
                groupIndent = indent;
            }

            if (groupIndent >= 0
                && indent == groupIndent
                && trimmed.StartsWith('-'))
            {
                if (groupStart >= 0)
                {
                    groups.Add((groupStart, index));
                }

                groupStart = index;
            }
        }

        foreach (var group in groups)
        {
            if (!ContainsGroupName(source, group.Start, group.End, UnitedStatesCommonGroupName))
            {
                continue;
            }

            var block = source.Skip(group.Start).Take(group.End - group.Start).ToArray();
            var rewritten = block[0].Contains('{') && block[0].Contains('}')
                ? RewriteInlineUnitedStatesGroup(block[0])
                : RewriteBlockUnitedStatesGroup(block, groupIndent);
            return source.Take(group.Start)
                .Concat(rewritten)
                .Concat(source.Skip(group.End))
                .ToArray();
        }

        return source.ToArray();
    }

    private static bool ContainsGroupName(
        string[] source,
        int start,
        int end,
        string expectedName)
    {
        for (var index = start; index < end; index++)
        {
            var line = source[index];
            if (line.Contains('{') && line.Contains('}'))
            {
                if (TryGetInlineMapValue(line, "name", out var inlineName)
                    && IsSameYamlScalar(inlineName, expectedName))
                {
                    return true;
                }

                continue;
            }

            if (TryGetMappingValue(line, "name", out var name)
                && IsSameYamlScalar(name, expectedName))
            {
                return true;
            }
        }

        return false;
    }

    private static string[] RewriteInlineUnitedStatesGroup(string line)
    {
        var openBrace = line.IndexOf('{');
        var closeBrace = line.LastIndexOf('}');
        if (openBrace < 0 || closeBrace <= openBrace)
        {
            return [line];
        }

        var entries = SplitInlineEntries(line[(openBrace + 1)..closeBrace])
            .Select(entry => entry.Trim())
            .Where(entry => entry.Length > 0)
            .Where(entry => !HasInlineKey(entry, "include-all"))
            .Where(entry => !HasInlineKey(entry, "filter"))
            .ToList();
        entries.Add("include-all: true");
        entries.Add($"filter: '{UnitedStatesProxyFilter}'");

        var content = string.Join(", ", entries);
        var rewritten = line[..(openBrace + 1)]
            + " "
            + content
            + " "
            + line[closeBrace..];
        return [rewritten];
    }

    private static string[] RewriteBlockUnitedStatesGroup(
        string[] block,
        int groupIndent)
    {
        var propertyIndent = FindGroupPropertyIndent(block, groupIndent);
        var includeAllFound = false;
        var filterFound = false;
        var rewritten = new List<string>(block.Length + 2);
        foreach (var line in block)
        {
            if (GetIndent(line) == propertyIndent
                && TryGetMappingKey(line, out var key)
                && key.Equals("include-all", StringComparison.OrdinalIgnoreCase))
            {
                rewritten.Add(new string(' ', propertyIndent) + "include-all: true");
                includeAllFound = true;
                continue;
            }

            if (GetIndent(line) == propertyIndent
                && TryGetMappingKey(line, out key)
                && key.Equals("filter", StringComparison.OrdinalIgnoreCase))
            {
                rewritten.Add(new string(' ', propertyIndent) + $"filter: '{UnitedStatesProxyFilter}'");
                filterFound = true;
                continue;
            }

            rewritten.Add(line);
        }

        var additions = new List<string>(2);
        if (!includeAllFound)
        {
            additions.Add(new string(' ', propertyIndent) + "include-all: true");
        }

        if (!filterFound)
        {
            additions.Add(new string(' ', propertyIndent) + $"filter: '{UnitedStatesProxyFilter}'");
        }

        if (additions.Count > 0)
        {
            rewritten.InsertRange(Math.Min(1, rewritten.Count), additions);
        }

        return rewritten.ToArray();
    }

    private static int FindGroupPropertyIndent(
        IReadOnlyList<string> block,
        int groupIndent)
    {
        foreach (var line in block.Skip(1))
        {
            var trimmed = line.TrimStart();
            if (trimmed.Length == 0 || trimmed.StartsWith('#'))
            {
                continue;
            }

            var indent = GetIndent(line);
            if (indent > groupIndent && !trimmed.StartsWith('-'))
            {
                return indent;
            }
        }

        return groupIndent + 2;
    }

    private static bool HasInlineKey(string entry, string expectedKey) =>
        TryGetInlineKey(entry, out var key)
        && key.Equals(expectedKey, StringComparison.OrdinalIgnoreCase);

    private static bool TryGetInlineKey(string entry, out string key) =>
        TryGetInlineKeyValue(entry, out key, out _);

    private static bool TryGetInlineMapValue(
        string line,
        string expectedKey,
        out string value)
    {
        var openBrace = line.IndexOf('{');
        var closeBrace = line.LastIndexOf('}');
        if (openBrace < 0 || closeBrace <= openBrace)
        {
            value = string.Empty;
            return false;
        }

        foreach (var entry in SplitInlineEntries(line[(openBrace + 1)..closeBrace]))
        {
            if (TryGetInlineKeyValue(entry, out var key, out var rawValue)
                && key.Equals(expectedKey, StringComparison.OrdinalIgnoreCase))
            {
                value = ParseYamlScalar(rawValue);
                return true;
            }
        }

        value = string.Empty;
        return false;
    }

    private static bool TryGetMappingValue(
        string line,
        string expectedKey,
        out string value)
    {
        if (!TryGetMappingKey(line, out var key)
            || !key.Equals(expectedKey, StringComparison.OrdinalIgnoreCase))
        {
            value = string.Empty;
            return false;
        }

        var trimmed = line.TrimStart();
        if (trimmed.StartsWith('-'))
        {
            trimmed = trimmed[1..].TrimStart();
        }

        var separator = trimmed.IndexOf(':');
        value = separator < 0
            ? string.Empty
            : ParseYamlScalar(trimmed[(separator + 1)..]);
        return separator >= 0;
    }

    private static bool TryGetMappingKey(string line, out string key)
    {
        var trimmed = line.TrimStart();
        if (trimmed.StartsWith('-'))
        {
            trimmed = trimmed[1..].TrimStart();
        }

        var separator = trimmed.IndexOf(':');
        if (separator <= 0 || trimmed.StartsWith('{'))
        {
            key = string.Empty;
            return false;
        }

        key = trimmed[..separator].Trim().Trim('\'', '"');
        return key.Length > 0;
    }

    private static bool TryGetInlineKeyValue(
        string entry,
        out string key,
        out string value)
    {
        var trimmed = entry.Trim();
        var separator = trimmed.IndexOf(':');
        if (separator <= 0)
        {
            key = string.Empty;
            value = string.Empty;
            return false;
        }

        key = trimmed[..separator].Trim().Trim('\'', '"');
        value = trimmed[(separator + 1)..].Trim();
        return key.Length > 0;
    }

    private static string ParseYamlScalar(string value)
    {
        var scalar = value.Trim().TrimEnd(',');
        if (scalar.Length >= 2
            && scalar[0] == '\''
            && scalar[^1] == '\'')
        {
            return scalar[1..^1].Replace("''", "'", StringComparison.Ordinal);
        }

        if (scalar.Length >= 2
            && scalar[0] == '"'
            && scalar[^1] == '"')
        {
            return scalar[1..^1];
        }

        var comment = scalar.IndexOf(" #", StringComparison.Ordinal);
        return (comment >= 0 ? scalar[..comment] : scalar).Trim();
    }

    private static bool IsSameYamlScalar(string value, string expected) =>
        ParseYamlScalar(value).Equals(expected, StringComparison.Ordinal);

    private static List<string> SplitInlineEntries(string content)
    {
        var entries = new List<string>();
        var start = 0;
        var depth = 0;
        char? quote = null;
        for (var index = 0; index < content.Length; index++)
        {
            var character = content[index];
            if (quote is not null)
            {
                if (character == '\\' && index + 1 < content.Length)
                {
                    index++;
                    continue;
                }

                if (character == quote)
                {
                    if (quote == '\'' && index + 1 < content.Length && content[index + 1] == '\'')
                    {
                        index++;
                        continue;
                    }

                    quote = null;
                }

                continue;
            }

            if (character is '\'' or '"')
            {
                quote = character;
                continue;
            }

            if (character is '[' or '{' or '(')
            {
                depth++;
                continue;
            }

            if (character is ']' or '}' or ')')
            {
                depth = Math.Max(0, depth - 1);
                continue;
            }

            if (character == ',' && depth == 0)
            {
                entries.Add(content[start..index]);
                start = index + 1;
            }
        }

        entries.Add(content[start..]);
        return entries;
    }

    private static List<string> ApplyTunOverride(string[] source, bool enabled)
    {
        var result = new List<string>(source.Length + 3);
        var inTunBlock = false;
        var tunIndent = -1;
        var tunHasEnable = false;
        var foundTunBlock = false;

        foreach (var line in source)
        {
            if (TryGetRootKey(line, out var key) && key.Equals("tun", StringComparison.OrdinalIgnoreCase))
            {
                if (inTunBlock && !tunHasEnable)
                {
                    result.Add(CreateTunEnableLine(tunIndent, enabled));
                }

                foundTunBlock = true;
                tunIndent = GetIndent(line);
                tunHasEnable = false;
                inTunBlock = !TryOverrideInlineTun(line, enabled, out var inlineLine, out tunHasEnable);
                result.Add(inlineLine);
                continue;
            }

            if (inTunBlock && IsRootBoundary(line, tunIndent))
            {
                if (!tunHasEnable)
                {
                    result.Add(CreateTunEnableLine(tunIndent, enabled));
                }

                inTunBlock = false;
            }

            if (inTunBlock && IsTunEnableLine(line, tunIndent))
            {
                result.Add(CreateTunEnableLine(GetIndent(line), enabled));
                tunHasEnable = true;
                continue;
            }

            result.Add(line);
        }

        if (inTunBlock && !tunHasEnable)
        {
            result.Add(CreateTunEnableLine(tunIndent, enabled));
        }

        if (!foundTunBlock)
        {
            result.Add(string.Empty);
            result.Add("tun:");
            result.Add(CreateTunEnableLine(2, enabled));
        }

        return result;
    }

    private static bool TryOverrideInlineTun(
        string line,
        bool enabled,
        out string result,
        out bool hasEnable)
    {
        var keySeparator = line.IndexOf(':');
        var valueStart = keySeparator < 0 ? -1 : line.IndexOf('{', keySeparator + 1);
        var valueEnd = valueStart < 0 ? -1 : line.LastIndexOf('}');
        if (valueStart < 0 || valueEnd <= valueStart)
        {
            result = line;
            hasEnable = false;
            return false;
        }

        var inlineValue = line[valueStart..(valueEnd + 1)];
        var enableSeparator = FindInlineKeySeparator(inlineValue, "enable");
        if (enableSeparator >= 0)
        {
            var valueIndex = enableSeparator + 1;
            while (valueIndex < inlineValue.Length && char.IsWhiteSpace(inlineValue[valueIndex]))
            {
                valueIndex++;
            }

            var valueEndIndex = valueIndex;
            while (valueEndIndex < inlineValue.Length
                && inlineValue[valueEndIndex] is not (',' or '}'))
            {
                valueEndIndex++;
            }

            inlineValue = inlineValue[..valueIndex]
                + (enabled ? "true" : "false")
                + inlineValue[valueEndIndex..];
            hasEnable = true;
        }
        else
        {
            inlineValue = inlineValue.Insert(1, $" enable: {(enabled ? "true" : "false")},");
            hasEnable = true;
        }

        result = line[..valueStart] + inlineValue + line[(valueEnd + 1)..];
        return true;
    }

    private static int FindInlineKeySeparator(string value, string expectedKey)
    {
        var position = 1;
        while (position < value.Length - 1)
        {
            while (position < value.Length && (char.IsWhiteSpace(value[position]) || value[position] == ','))
            {
                position++;
            }

            var keyStart = position;
            while (position < value.Length && value[position] is not (':' or ',' or '}'))
            {
                position++;
            }

            if (position >= value.Length || value[position] != ':')
            {
                break;
            }

            var key = value[keyStart..position].Trim().Trim('\'', '"');
            if (key.Equals(expectedKey, StringComparison.OrdinalIgnoreCase))
            {
                return position;
            }

            position++;
            while (position < value.Length && value[position] is not (',' or '}'))
            {
                position++;
            }
        }

        return -1;
    }

    private static bool IsTunEnableLine(string line, int tunIndent)
    {
        var indent = GetIndent(line);
        if (indent <= tunIndent)
        {
            return false;
        }

        var trimmed = line.TrimStart();
        if (trimmed.Length == 0 || trimmed.StartsWith('#'))
        {
            return false;
        }

        var separator = trimmed.IndexOf(':');
        return separator > 0
            && trimmed[..separator].Trim().Equals("enable", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsRootBoundary(string line, int tunIndent)
    {
        var trimmed = line.TrimStart();
        return trimmed.Length > 0
            && !trimmed.StartsWith('#')
            && GetIndent(line) <= tunIndent;
    }

    private static bool TryGetRootKey(string line, out string key)
    {
        key = string.Empty;
        if (line.Length > 0 && char.IsWhiteSpace(line[0]))
        {
            return false;
        }

        var trimmed = line.Trim();
        var separator = trimmed.IndexOf(':');
        if (separator <= 0 || trimmed.StartsWith('#') || trimmed is "---" or "...")
        {
            return false;
        }

        key = trimmed[..separator].Trim().Trim('\'', '"');
        return key.Length > 0;
    }

    private static int GetIndent(string line) => line.Length - line.TrimStart().Length;

    private static string CreateTunEnableLine(int indent, bool enabled) =>
        new string(' ', Math.Max(0, indent)) + $"enable: {(enabled ? "true" : "false")}";

    private static bool IsManagedLine(string line)
    {
        if (line.Length > 0 && char.IsWhiteSpace(line[0]))
        {
            return false;
        }

        var trimmed = line.TrimStart();
        return trimmed.StartsWith("external-controller:", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("secret:", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("allow-lan:", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("ipv6:", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("tcp-concurrent:", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("log-level:", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("port:", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("mixed-port:", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("socks-port:", StringComparison.OrdinalIgnoreCase);
    }
}

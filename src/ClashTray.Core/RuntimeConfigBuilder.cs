using System.Text;
using ClashTray.Contracts;

namespace ClashTray.Core;

public static class RuntimeConfigBuilder
{
    public static async Task<string> BuildAsync(
        string sourcePath,
        string destinationPath,
        AppSettings settings,
        CancellationToken cancellationToken = default)
    {
        SettingsValidator.Validate(settings);
        var source = await File.ReadAllLinesAsync(sourcePath, cancellationToken);
        var withTunOverride = ApplyTunOverride(source, settings.TunEnabled);
        var filtered = withTunOverride.Where(line => !IsManagedLine(line)).ToList();
        filtered.Add(string.Empty);
        filtered.Add($"external-controller: 127.0.0.1:{settings.ControllerPort}");
        filtered.Add("secret: ''");
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

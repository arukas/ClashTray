using System.Text;
using ClashTray.Contracts;

namespace ClashTray.Core;

public static class RuntimeConfigBuilder
{
    public static Task<string> BuildAsync(
        string sourcePath,
        string destinationPath,
        AppSettings settings,
        CancellationToken cancellationToken = default)
        => BuildAsync(
            sourcePath,
            destinationPath,
            settings,
            externalUiPath: null,
            cancellationToken: cancellationToken);

    /// <summary>
    /// Builds the effective process configuration with TUN explicitly off.
    /// The user's source configuration is never changed; TUN is enabled later
    /// only through the service transaction after the core is healthy.
    /// </summary>
    public static Task<string> BuildForCoreStartAsync(
        string sourcePath,
        string destinationPath,
        AppSettings settings,
        string? externalUiPath,
        CancellationToken cancellationToken = default) =>
        BuildAsyncCore(
            sourcePath,
            destinationPath,
            settings,
            externalUiPath,
            tunEnabled: false,
            cancellationToken: cancellationToken);

    public static Task<string> BuildAsync(
        string sourcePath,
        string destinationPath,
        AppSettings settings,
        string? externalUiPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return BuildAsyncCore(
            sourcePath,
            destinationPath,
            settings,
            externalUiPath,
            settings.TunEnabled,
            cancellationToken);
    }

    private static async Task<string> BuildAsyncCore(
        string sourcePath,
        string destinationPath,
        AppSettings settings,
        string? externalUiPath,
        bool tunEnabled,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sourcePath);
        ArgumentNullException.ThrowIfNull(destinationPath);
        ArgumentNullException.ThrowIfNull(settings);
        SettingsValidator.Validate(settings);
        string[] source = await File.ReadAllLinesAsync(sourcePath, cancellationToken);
        List<string> withTunOverride = ApplyTunOverride(source, tunEnabled, settings.TunStack);
        List<string> filtered = withTunOverride.Where(line => !IsManagedLine(line)).ToList();
        filtered.Add(string.Empty);
        filtered.Add($"external-controller: 127.0.0.1:{settings.ControllerPort}");
        filtered.Add("secret: ''");
        if (TryResolveExternalUiPath(externalUiPath, out string resolvedExternalUiPath))
        {
            filtered.Add($"external-ui: '{EscapeYamlSingleQuoted(resolvedExternalUiPath)}'");
        }

        filtered.Add($"allow-lan: {(settings.AllowLan ? "true" : "false")}");
        filtered.Add($"ipv6: {(settings.Ipv6 ? "true" : "false")}");
        filtered.Add($"tcp-concurrent: {(settings.TcpConcurrent ? "true" : "false")}");
        filtered.Add($"log-level: {settings.LogLevel.Trim().ToLowerInvariant()}");
        filtered.Add($"port: {settings.HttpPort}");
        filtered.Add($"mixed-port: {settings.MixedPort}");
        filtered.Add($"socks-port: {settings.SocksPort}");

        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        string tempPath = $"{destinationPath}.{Guid.NewGuid():N}.tmp";
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

    private static List<string> ApplyTunOverride(string[] source, bool enabled, string stack)
    {
        string? managedStack = string.Equals(stack, "configuration", StringComparison.OrdinalIgnoreCase)
            ? null
            : stack.Trim().ToLowerInvariant();
        List<string> result = new List<string>(source.Length + 4);
        bool inTunBlock = false;
        int tunIndent = -1;
        bool tunHasEnable = false;
        bool tunHasStack = false;
        bool foundTunBlock = false;

        foreach (string line in source)
        {
            if (TryGetRootKey(line, out string? key) && key.Equals("tun", StringComparison.OrdinalIgnoreCase))
            {
                if (inTunBlock)
                {
                    AppendMissingTunProperties(
                        result,
                        tunIndent + 2,
                        enabled,
                        tunHasEnable,
                        managedStack,
                        tunHasStack);
                }

                foundTunBlock = true;
                tunIndent = GetIndent(line);
                tunHasEnable = false;
                tunHasStack = false;
                inTunBlock = !TryOverrideInlineTun(
                    line,
                    enabled,
                    managedStack,
                    out string? inlineLine,
                    out tunHasEnable,
                    out tunHasStack);
                result.Add(inlineLine);
                continue;
            }

            if (inTunBlock && IsRootBoundary(line, tunIndent))
            {
                AppendMissingTunProperties(
                    result,
                    tunIndent + 2,
                    enabled,
                    tunHasEnable,
                    managedStack,
                    tunHasStack);

                inTunBlock = false;
            }

            if (inTunBlock && IsTunPropertyLine(line, tunIndent, "enable"))
            {
                result.Add(CreateTunPropertyLine(GetIndent(line), "enable", enabled ? "true" : "false"));
                tunHasEnable = true;
                continue;
            }

            if (inTunBlock
                && managedStack is not null
                && IsTunPropertyLine(line, tunIndent, "stack"))
            {
                result.Add(CreateTunPropertyLine(GetIndent(line), "stack", managedStack));
                tunHasStack = true;
                continue;
            }

            result.Add(line);
        }

        if (inTunBlock)
        {
            AppendMissingTunProperties(
                result,
                tunIndent + 2,
                enabled,
                tunHasEnable,
                managedStack,
                tunHasStack);
        }

        if (!foundTunBlock)
        {
            result.Add(string.Empty);
            result.Add("tun:");
            result.Add(CreateTunPropertyLine(2, "enable", enabled ? "true" : "false"));
            if (managedStack is not null)
            {
                result.Add(CreateTunPropertyLine(2, "stack", managedStack));
            }
        }

        return result;
    }

    private static void AppendMissingTunProperties(
        List<string> result,
        int indent,
        bool enabled,
        bool hasEnable,
        string? managedStack,
        bool hasStack)
    {
        if (!hasEnable)
        {
            result.Add(CreateTunPropertyLine(indent, "enable", enabled ? "true" : "false"));
        }

        if (managedStack is not null && !hasStack)
        {
            result.Add(CreateTunPropertyLine(indent, "stack", managedStack));
        }
    }

    private static bool TryOverrideInlineTun(
        string line,
        bool enabled,
        string? managedStack,
        out string result,
        out bool hasEnable,
        out bool hasStack)
    {
        int keySeparator = line.IndexOf(':', StringComparison.Ordinal);
        int valueStart = keySeparator < 0 ? -1 : line.IndexOf('{', keySeparator + 1);
        int valueEnd = valueStart < 0 ? -1 : line.LastIndexOf('}');
        if (valueStart < 0 || valueEnd <= valueStart)
        {
            result = line;
            hasEnable = false;
            hasStack = false;
            return false;
        }

        string inlineValue = line[valueStart..(valueEnd + 1)];
        inlineValue = SetInlineScalar(
            inlineValue,
            "enable",
            enabled ? "true" : "false",
            out hasEnable);
        if (managedStack is not null)
        {
            inlineValue = SetInlineScalar(inlineValue, "stack", managedStack, out hasStack);
        }
        else
        {
            hasStack = FindInlineKeySeparator(inlineValue, "stack") >= 0;
        }

        result = line[..valueStart] + inlineValue + line[(valueEnd + 1)..];
        return true;
    }

    private static string SetInlineScalar(
        string inlineValue,
        string property,
        string value,
        out bool hasProperty)
    {
        int separator = FindInlineKeySeparator(inlineValue, property);
        if (separator >= 0)
        {
            int valueIndex = separator + 1;
            while (valueIndex < inlineValue.Length && char.IsWhiteSpace(inlineValue[valueIndex]))
            {
                valueIndex++;
            }

            int valueEndIndex = valueIndex;
            while (valueEndIndex < inlineValue.Length
                && inlineValue[valueEndIndex] is not (',' or '}'))
            {
                valueEndIndex++;
            }

            hasProperty = true;
            return inlineValue[..valueIndex] + value + inlineValue[valueEndIndex..];
        }

        hasProperty = true;
        return inlineValue.Insert(1, $" {property}: {value},");
    }

    private static int FindInlineKeySeparator(string value, string expectedKey)
    {
        int position = 1;
        while (position < value.Length - 1)
        {
            while (position < value.Length && (char.IsWhiteSpace(value[position]) || value[position] == ','))
            {
                position++;
            }

            int keyStart = position;
            while (position < value.Length && value[position] is not (':' or ',' or '}'))
            {
                position++;
            }

            if (position >= value.Length || value[position] != ':')
            {
                break;
            }

            string key = value[keyStart..position].Trim().Trim('\'', '"');
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

    private static bool IsTunPropertyLine(string line, int tunIndent, string property)
    {
        int indent = GetIndent(line);
        if (indent <= tunIndent)
        {
            return false;
        }

        string trimmed = line.TrimStart();
        if (trimmed.Length == 0 || trimmed.StartsWith('#'))
        {
            return false;
        }

        int separator = trimmed.IndexOf(':', StringComparison.Ordinal);
        return separator > 0
            && trimmed[..separator].Trim().Trim('\'', '"').Equals(property, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsRootBoundary(string line, int tunIndent)
    {
        string trimmed = line.TrimStart();
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

        string trimmed = line.Trim();
        int separator = trimmed.IndexOf(':', StringComparison.Ordinal);
        if (separator <= 0 || trimmed.StartsWith('#') || trimmed is "---" or "...")
        {
            return false;
        }

        key = trimmed[..separator].Trim().Trim('\'', '"');
        return key.Length > 0;
    }

    private static int GetIndent(string line) => line.Length - line.TrimStart().Length;

    private static string CreateTunPropertyLine(int indent, string property, string value) =>
        new string(' ', Math.Max(0, indent)) + $"{property}: {value}";

    private static bool IsManagedLine(string line)
    {
        if (line.Length > 0 && char.IsWhiteSpace(line[0]))
        {
            return false;
        }

        string trimmed = line.TrimStart();
        return trimmed.StartsWith("external-controller:", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("secret:", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("external-ui:", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("external-ui-name:", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("external-ui-url:", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("allow-lan:", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("ipv6:", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("tcp-concurrent:", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("log-level:", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("port:", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("mixed-port:", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("socks-port:", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryResolveExternalUiPath(string? path, out string resolvedPath)
    {
        resolvedPath = string.Empty;
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
        {
            return false;
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(path);
        }
        catch (ArgumentException)
        {
            return false;
        }

        if (!Directory.Exists(fullPath)
            || !File.Exists(Path.Combine(fullPath, "index.html")))
        {
            return false;
        }

        resolvedPath = fullPath;
        return true;
    }

    private static string EscapeYamlSingleQuoted(string value) => value.Replace("'", "''", StringComparison.Ordinal);
}

using System.Text;
using static ClashTray.Core.YamlStructure;
using ClashTray.Contracts;

namespace ClashTray.Core;

public static class RuntimeConfigBuilder
{
    public const int MaximumInputBytes = 16 * 1024 * 1024;
    public const int MaximumInputLines = 250_000;
    public const int MaximumLineCharacters = 64 * 1024;

    private const int CancellationCheckLineMask = 0x3F;
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

    /// <summary>
    /// Writes a service-owned candidate runtime config with the controller
    /// pinned to loopback and the only supported controller secret (empty).
    /// The managed listener settings and user profile are otherwise preserved.
    /// </summary>
    public static async Task<string> BuildControllerCandidateAsync(
        string sourcePath,
        string destinationPath,
        int controllerPort,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        if (controllerPort is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(controllerPort));
        }

        FileInfo sourceInfo = new(sourcePath);
        if (!sourceInfo.Exists || sourceInfo.Length > MaximumInputBytes)
        {
            throw new InvalidDataException("The managed runtime configuration is missing or exceeds its size limit.");
        }

        string[] source = await BoundedYamlReader.ReadFileAsync(sourcePath, cancellationToken).ConfigureAwait(false);
        if (source.Length > MaximumInputLines || source.Any(line => line.Length > MaximumLineCharacters))
        {
            throw new InvalidDataException("The managed runtime configuration exceeds its line limits.");
        }

        YamlDocumentScope scope = YamlStructureDocument.Read(source, cancellationToken).RequireMappingScope();
        List<string> filtered = RemoveSpecificRootEntries(
            source.ToList(),
            new HashSet<string>(["external-controller", "secret"], StringComparer.OrdinalIgnoreCase),
            cancellationToken);
        List<string> managed =
        [
            string.Empty,
            $"external-controller: 127.0.0.1:{controllerPort}",
            "secret: ''"
        ];
        InsertBeforeDocumentEnd(filtered, managed, scope);

        string? directory = Path.GetDirectoryName(destinationPath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new InvalidOperationException("A managed candidate configuration must have a parent directory.");
        }

        Directory.CreateDirectory(directory);
        string temporaryPath = $"{destinationPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllLinesAsync(
                temporaryPath,
                filtered,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                cancellationToken).ConfigureAwait(false);
            WindowsPathSecurity.ProtectRuntimeFile(temporaryPath);
            File.Move(temporaryPath, destinationPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }

        return destinationPath;
    }

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

    private static void InsertBeforeDocumentEnd(
        List<string> destination,
        List<string> additions,
        YamlDocumentScope documentScope)
    {
        if (additions.Count == 0)
        {
            return;
        }

        int insertionIndex = documentScope.EndMarkerLine >= 0
            ? destination.FindIndex(IsYamlDocumentEndMarker)
            : destination.Count;
        if (insertionIndex < 0)
        {
            throw new InvalidDataException(
                "The YAML document end marker was lost while building the managed runtime configuration.");
        }

        if (insertionIndex > 0
            && !string.IsNullOrWhiteSpace(destination[insertionIndex - 1])
            && (additions.Count == 0 || !string.IsNullOrWhiteSpace(additions[0])))
        {
            additions.Insert(0, string.Empty);
        }

        destination.InsertRange(insertionIndex, additions);
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
        cancellationToken.ThrowIfCancellationRequested();

        string[] source = await BoundedYamlReader.ReadFileAsync(sourcePath, cancellationToken).ConfigureAwait(false);

        YamlDocumentScope documentScope = YamlStructureDocument.Read(source, cancellationToken).RequireMappingScope();
        List<string> withTunOverride = ApplyTunOverride(
            source,
            documentScope,
            tunEnabled,
            settings.TunStack,
            cancellationToken);
        List<string> filtered = RemoveManagedRootEntries(withTunOverride, cancellationToken);
        List<string> managedSettings = [string.Empty, $"external-controller: 127.0.0.1:{settings.ControllerPort}", "secret: ''"];
        if (TryResolveExternalUiPath(externalUiPath, out string resolvedExternalUiPath))
        {
            managedSettings.Add($"external-ui: '{EscapeYamlSingleQuoted(resolvedExternalUiPath)}'");
        }

        managedSettings.Add($"allow-lan: {(settings.AllowLan ? "true" : "false")}");
        managedSettings.Add($"ipv6: {(settings.Ipv6 ? "true" : "false")}");
        managedSettings.Add($"tcp-concurrent: {(settings.TcpConcurrent ? "true" : "false")}");
        managedSettings.Add($"log-level: {settings.LogLevel.Trim().ToLowerInvariant()}");
        managedSettings.Add($"port: {settings.HttpPort}");
        managedSettings.Add($"mixed-port: {settings.MixedPort}");
        managedSettings.Add($"socks-port: {settings.SocksPort}");
        InsertBeforeDocumentEnd(filtered, managedSettings, documentScope);

        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        string tempPath = $"{destinationPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllLinesAsync(
                tempPath,
                filtered,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                cancellationToken);
            WindowsPathSecurity.ProtectRuntimeFile(tempPath);
            File.Move(tempPath, destinationPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }

        return destinationPath;
    }

    private static List<string> ApplyTunOverride(
        string[] source,
        YamlDocumentScope documentScope,
        bool enabled,
        string stack,
        CancellationToken cancellationToken)
    {
        string? managedStack = string.Equals(stack, "configuration", StringComparison.OrdinalIgnoreCase)
            ? null
            : stack.Trim().ToLowerInvariant();
        List<string> result = new(source.Length + 4);
        bool inTunBlock = false;
        int tunIndent = -1;
        int tunChildIndent = -1;
        bool tunHasEnable = false;
        bool tunHasStack = false;
        bool foundTunBlock = false;

        for (int index = 0; index < source.Length; index++)
        {
            if ((index & CancellationCheckLineMask) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            string line = source[index];
            if (TryGetRootKey(line, out string? key) && key.Equals("tun", StringComparison.OrdinalIgnoreCase))
            {
                if (inTunBlock)
                {
                    AppendMissingTunProperties(
                        result,
                        GetTunChildIndent(tunIndent, tunChildIndent),
                        enabled,
                        tunHasEnable,
                        managedStack,
                        tunHasStack);
                }

                EnsureSupportedTunRootValue(line);
                foundTunBlock = true;
                tunIndent = GetIndent(line);
                tunChildIndent = -1;
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
                    GetTunChildIndent(tunIndent, tunChildIndent),
                    enabled,
                    tunHasEnable,
                    managedStack,
                    tunHasStack);

                inTunBlock = false;
            }

            if (inTunBlock && tunChildIndent < 0 && IsTunChildContent(line, tunIndent))
            {
                tunChildIndent = GetIndent(line);
            }

            if (inTunBlock && IsTunPropertyLine(line, tunChildIndent, "enable"))
            {
                YamlNodeRange range = FindYamlNodeRange(source, index, "enable", cancellationToken);
                result.Add(ReplaceTunPropertyLine(line, "enable", enabled ? "true" : "false"));
                tunHasEnable = true;
                index = range.LastLine;
                continue;
            }

            if (inTunBlock
                && managedStack is not null
                && IsTunPropertyLine(line, tunChildIndent, "stack"))
            {
                YamlNodeRange range = FindYamlNodeRange(source, index, "stack", cancellationToken);
                result.Add(ReplaceTunPropertyLine(line, "stack", managedStack));
                tunHasStack = true;
                index = range.LastLine;
                continue;
            }

            result.Add(line);
        }

        if (inTunBlock)
        {
            AppendMissingTunProperties(
                result,
                GetTunChildIndent(tunIndent, tunChildIndent),
                enabled,
                tunHasEnable,
                managedStack,
                tunHasStack);
        }

        if (!foundTunBlock)
        {
            List<string> newTun = [string.Empty, new string(' ', documentScope.RootIndent) + "tun:", CreateTunPropertyLine(documentScope.RootIndent + 2, "enable", enabled ? "true" : "false")];
            if (managedStack is not null)
            {
                newTun.Add(CreateTunPropertyLine(documentScope.RootIndent + 2, "stack", managedStack));
            }

            InsertBeforeDocumentEnd(result, newTun, documentScope);
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
        int keySeparator = FindYamlKeySeparator(line);
        int valueStart = keySeparator < 0 ? -1 : keySeparator + 1;
        while (valueStart >= 0 && valueStart < line.Length && char.IsWhiteSpace(line[valueStart]))
        {
            valueStart++;
        }

        if (valueStart < 0 || valueStart >= line.Length || line[valueStart] != '{')
        {
            result = line;
            hasEnable = false;
            hasStack = false;
            return false;
        }

        int valueEnd = FindMatchingFlowMapEnd(line, valueStart);
        if (valueEnd < 0)
        {
            throw new InvalidDataException("TUN inline mapping is not balanced; use a valid flow mapping or block mapping.");
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

            int valueEndIndex = FindInlineEntryEnd(inlineValue, valueIndex);
            while (valueEndIndex > valueIndex && char.IsWhiteSpace(inlineValue[valueEndIndex - 1]))
            {
                valueEndIndex--;
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

            int entryEnd = FindInlineEntryEnd(value, position);
            if (entryEnd <= position)
            {
                break;
            }

            string entry = value[position..entryEnd];
            int separator = FindYamlKeySeparator(entry);
            if (separator <= 0)
            {
                break;
            }

            if (!YamlMappingKeyReader.TryDecode(entry[..separator].Trim(), out string key))
            {
                throw new InvalidDataException("TUN mapping contains an unsupported key; use simple scalar keys.");
            }
            if (key.Equals(expectedKey, StringComparison.OrdinalIgnoreCase))
            {
                return position + separator;
            }

            if (entryEnd >= value.Length || value[entryEnd] == '}')
            {
                break;
            }

            position = entryEnd + 1;
        }

        return -1;
    }

    private static bool IsTunPropertyLine(string line, int childIndent, string property)
    {
        if (childIndent < 0 || GetIndent(line) != childIndent)
        {
            return false;
        }

        string trimmed = line.TrimStart();
        if (trimmed.Length == 0 || trimmed.StartsWith('#'))
        {
            return false;
        }

        int separator = FindYamlKeySeparator(trimmed);
        return separator > 0
            && YamlMappingKeyReader.TryDecode(trimmed[..separator].Trim(), out string key)
            && key.Equals(property, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsTunChildContent(string line, int tunIndent)
    {
        string trimmed = line.TrimStart();
        return trimmed.Length > 0
            && !trimmed.StartsWith('#')
            && GetIndent(line) > tunIndent;
    }

    private static int GetTunChildIndent(int tunIndent, int childIndent) =>
        childIndent > tunIndent ? childIndent : tunIndent + 2;

    private static string ReplaceTunPropertyLine(string line, string property, string value)
    {
        string replacement = CreateTunPropertyLine(GetIndent(line), property, value);
        int commentStart = FindInlineCommentStart(line);
        if (commentStart < 0)
        {
            return replacement;
        }

        int suffixStart = commentStart;
        while (suffixStart > 0 && char.IsWhiteSpace(line[suffixStart - 1]))
        {
            suffixStart--;
        }

        return replacement + line[suffixStart..];
    }

    private static bool IsRootBoundary(string line, int tunIndent)
    {
        string trimmed = line.TrimStart();
        return trimmed.Length > 0
            && !trimmed.StartsWith('#')
            && GetIndent(line) <= tunIndent;
    }

    private static void EnsureSupportedTunRootValue(string line)
    {
        string value = GetRootValue(line);
        if (value.Length == 0)
        {
            return;
        }

        if (value.StartsWith('*') || value.StartsWith('&'))
        {
            throw new InvalidDataException(
                "TUN anchor or alias values cannot be safely overridden; use a standalone block or inline mapping.");
        }

        if (value[0] == '{' && IsFlowCollectionBalanced(value))
        {
            return;
        }

        throw new InvalidDataException(
            "TUN must use a block mapping or a balanced inline mapping before its managed settings can be changed.");
    }

    private static List<string> RemoveManagedRootEntries(
        List<string> source,
        CancellationToken cancellationToken)
    {
        List<string> result = new(source.Count + 12);
        YamlStructureDocument structure = YamlStructureDocument.Read(source, cancellationToken);
        Dictionary<int, YamlRootNode> nodes = structure.Roots.ToDictionary(node => node.Section.FirstLine);
        for (int index = 0; index < source.Count;)
        {
            if ((index & CancellationCheckLineMask) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            string line = source[index];
            if (!nodes.TryGetValue(index, out YamlRootNode? node) || !IsManagedRootKey(node.Key))
            {
                result.Add(line);
                index++;
                continue;
            }

            YamlNodeRange range = structure.ReplacementSpan(node, cancellationToken);
            index = range.LastLine + 1;
        }

        return result;
    }

    private static List<string> RemoveSpecificRootEntries(
        List<string> source,
        HashSet<string> keys,
        CancellationToken cancellationToken)
    {
        List<string> result = new(source.Count + keys.Count);
        YamlStructureDocument structure = YamlStructureDocument.Read(source, cancellationToken);
        Dictionary<int, YamlRootNode> nodes = structure.Roots.ToDictionary(node => node.Section.FirstLine);
        for (int index = 0; index < source.Count;)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string line = source[index];
            if (!nodes.TryGetValue(index, out YamlRootNode? node) || !keys.Contains(node.Key))
            {
                result.Add(line);
                index++;
                continue;
            }

            YamlNodeRange range = structure.ReplacementSpan(node, cancellationToken);
            index = range.LastLine + 1;
        }

        return result;
    }

    internal static int FindQuotedScalarEnd(IReadOnlyList<string> source, int firstLine, int parentIndent, char quote, CancellationToken cancellationToken) =>
        YamlStructure.FindQuotedScalarEnd(source, firstLine, parentIndent, quote, cancellationToken);

    private static bool IsManagedRootKey(string key) =>
        key.Equals("external-controller", StringComparison.OrdinalIgnoreCase)
        || key.Equals("secret", StringComparison.OrdinalIgnoreCase)
        || key.Equals("external-ui", StringComparison.OrdinalIgnoreCase)
        || key.Equals("external-ui-name", StringComparison.OrdinalIgnoreCase)
        || key.Equals("external-ui-url", StringComparison.OrdinalIgnoreCase)
        || key.Equals("allow-lan", StringComparison.OrdinalIgnoreCase)
        || key.Equals("ipv6", StringComparison.OrdinalIgnoreCase)
        || key.Equals("tcp-concurrent", StringComparison.OrdinalIgnoreCase)
        || key.Equals("log-level", StringComparison.OrdinalIgnoreCase)
        || key.Equals("port", StringComparison.OrdinalIgnoreCase)
        || key.Equals("mixed-port", StringComparison.OrdinalIgnoreCase)
        || key.Equals("socks-port", StringComparison.OrdinalIgnoreCase);



    private static string CreateTunPropertyLine(int indent, string property, string value) =>
        new string(' ', Math.Max(0, indent)) + $"{property}: {value}";
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

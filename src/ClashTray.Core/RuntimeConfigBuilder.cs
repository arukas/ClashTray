using System.Text;
using ClashTray.Contracts;

namespace ClashTray.Core;

public static class RuntimeConfigBuilder
{
    public const int MaximumInputBytes = 16 * 1024 * 1024;
    public const int MaximumInputLines = 250_000;
    public const int MaximumLineCharacters = 64 * 1024;

    private const int CancellationCheckLineMask = 0x3F;
    private const int CancellationCheckCharacterMask = 0xFFF;
    private const int MaximumFlowCollectionDepth = 128;

    private readonly record struct YamlDocumentScope(
        int RootIndent,
        int EndMarkerLine,
        bool HasExplicitStartMarker);

    private readonly record struct YamlNodeRange(int FirstLine, int LastLine);
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

    private static YamlDocumentScope ValidateYamlDocumentScope(
        string[] source,
        CancellationToken cancellationToken)
    {
        bool hasExplicitStartMarker = false;
        bool hasDocumentContent = false;
        bool ended = false;
        int endMarkerLine = -1;
        for (int index = 0; index < source.Length; index++)
        {
            if ((index & CancellationCheckLineMask) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            string line = source[index];
            if (IsYamlDocumentStartMarker(line))
            {
                if (hasExplicitStartMarker || hasDocumentContent || ended)
                {
                    throw new InvalidDataException(
                        "Multiple YAML documents are not supported by the managed runtime configuration builder.");
                }

                hasExplicitStartMarker = true;
                continue;
            }

            if (IsYamlDocumentEndMarker(line))
            {
                if (ended)
                {
                    throw new InvalidDataException("The YAML document has more than one end marker.");
                }

                ended = true;
                endMarkerLine = index;
                continue;
            }

            string trimmed = line.TrimStart();
            if (trimmed.Length == 0 || trimmed.StartsWith('#'))
            {
                continue;
            }

            if (ended)
            {
                throw new InvalidDataException(
                    "Non-comment YAML content follows the explicit document end marker.");
            }

            int indentation = GetIndent(line);
            if (indentation > 0)
            {
                if (!hasDocumentContent)
                {
                    throw new InvalidDataException(
                        "An overall-indented YAML root mapping is not supported; remove the leading indentation and retry.");
                }

                continue;
            }

            if (!TryGetRootKey(line, out _))
            {
                throw new InvalidDataException(
                    "The managed runtime configuration builder requires a single block-mapping YAML document.");
            }

            hasDocumentContent = true;
        }

        return new YamlDocumentScope(RootIndent: 0, endMarkerLine, hasExplicitStartMarker);
    }

    private static bool IsYamlDocumentStartMarker(string line) =>
        IsYamlDocumentMarker(line, "---");

    private static bool IsYamlDocumentEndMarker(string line) =>
        IsYamlDocumentMarker(line, "...");

    private static bool IsYamlDocumentMarker(string line, string marker)
    {
        if (line.Length < marker.Length
            || !line.AsSpan().StartsWith(marker, StringComparison.Ordinal))
        {
            return false;
        }

        return line.Length == marker.Length || char.IsWhiteSpace(line[marker.Length]);
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

        FileInfo sourceInfo = new(sourcePath);
        if (sourceInfo.Exists && sourceInfo.Length > MaximumInputBytes)
        {
            throw new InvalidDataException($"Configuration exceeds the {MaximumInputBytes}-byte runtime builder limit.");
        }

        string[] source = await File.ReadAllLinesAsync(sourcePath, cancellationToken);
        if (source.Length > MaximumInputLines)
        {
            throw new InvalidDataException($"Configuration exceeds the {MaximumInputLines}-line runtime builder limit.");
        }

        for (int index = 0; index < source.Length; index++)
        {
            if ((index & CancellationCheckLineMask) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            if (source[index].Length > MaximumLineCharacters)
            {
                throw new InvalidDataException(
                    $"Configuration line {index + 1} exceeds the {MaximumLineCharacters}-character runtime builder limit.");
            }
        }

        sourceInfo.Refresh();
        if (sourceInfo.Exists && sourceInfo.Length > MaximumInputBytes)
        {
            throw new InvalidDataException($"Configuration exceeds the {MaximumInputBytes}-byte runtime builder limit.");
        }

        YamlDocumentScope documentScope = ValidateYamlDocumentScope(source, cancellationToken);
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

            string key = entry[..separator].Trim().Trim('\'', '"');
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

    private static int FindInlineEntryEnd(string value, int start)
    {
        char quote = '\0';
        bool escaped = false;
        List<char> delimiters = [];
        for (int index = start; index < value.Length; index++)
        {
            char current = value[index];
            if (quote == '"')
            {
                if (escaped)
                {
                    escaped = false;
                }
                else if (current == '\\')
                {
                    escaped = true;
                }
                else if (current == '"')
                {
                    quote = '\0';
                }

                continue;
            }

            if (quote == '\'')
            {
                if (current == '\'' && index + 1 < value.Length && value[index + 1] == '\'')
                {
                    index++;
                }
                else if (current == '\'')
                {
                    quote = '\0';
                }

                continue;
            }

            if (current is '"' or '\'')
            {
                quote = current;
                continue;
            }

            if (current is '{' or '[' or '(')
            {
                delimiters.Add(current);
                continue;
            }

            if (current is '}' or ']' or ')')
            {
                if (delimiters.Count == 0)
                {
                    return current == '}' ? index : value.Length;
                }

                char expectedOpening = current switch
                {
                    '}' => '{',
                    ']' => '[',
                    _ => '('
                };
                if (delimiters[^1] != expectedOpening)
                {
                    return value.Length;
                }

                delimiters.RemoveAt(delimiters.Count - 1);
                continue;
            }

            if (current == ',' && delimiters.Count == 0)
            {
                return index;
            }
        }

        return value.Length;
    }

    private static int FindMatchingFlowMapEnd(string value, int start)
    {
        char quote = '\0';
        bool escaped = false;
        int depth = 0;
        for (int index = start; index < value.Length; index++)
        {
            char current = value[index];
            if (quote == '"')
            {
                if (escaped)
                {
                    escaped = false;
                }
                else if (current == '\\')
                {
                    escaped = true;
                }
                else if (current == '"')
                {
                    quote = '\0';
                }

                continue;
            }

            if (quote == '\'')
            {
                if (current == '\'' && index + 1 < value.Length && value[index + 1] == '\'')
                {
                    index++;
                }
                else if (current == '\'')
                {
                    quote = '\0';
                }

                continue;
            }

            if (current is '"' or '\'')
            {
                quote = current;
            }
            else if (current == '{')
            {
                depth++;
            }
            else if (current == '}' && --depth == 0)
            {
                return index;
            }
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
            && trimmed[..separator].Trim().Trim('\'', '"').Equals(property, StringComparison.OrdinalIgnoreCase);
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

    private static int FindInlineCommentStart(string line)
    {
        char quote = '\0';
        bool escaped = false;
        for (int index = 0; index < line.Length; index++)
        {
            char current = line[index];
            if (quote == '"')
            {
                if (escaped)
                {
                    escaped = false;
                }
                else if (current == '\\')
                {
                    escaped = true;
                }
                else if (current == '"')
                {
                    quote = '\0';
                }

                continue;
            }

            if (quote == '\'')
            {
                if (current == '\'' && index + 1 < line.Length && line[index + 1] == '\'')
                {
                    index++;
                }
                else if (current == '\'')
                {
                    quote = '\0';
                }

                continue;
            }

            if (current is '"' or '\'')
            {
                quote = current;
            }
            else if (current == '#'
                && (index == 0 || char.IsWhiteSpace(line[index - 1])))
            {
                return index;
            }
        }

        return -1;
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
        if (trimmed.Length == 0 || trimmed.StartsWith('#') || trimmed is "---" or "...")
        {
            return false;
        }

        int separator = FindYamlKeySeparator(trimmed);
        if (separator <= 0)
        {
            return false;
        }

        key = trimmed[..separator].Trim().Trim('\'', '"');
        return key.Length > 0;
    }

    private static int FindYamlKeySeparator(string value)
    {
        char quote = '\0';
        bool escaped = false;
        for (int index = 0; index < value.Length; index++)
        {
            char current = value[index];
            if (quote == '"')
            {
                if (escaped)
                {
                    escaped = false;
                }
                else if (current == '\\')
                {
                    escaped = true;
                }
                else if (current == '"')
                {
                    quote = '\0';
                }

                continue;
            }

            if (quote == '\'')
            {
                if (current == '\'' && index + 1 < value.Length && value[index + 1] == '\'')
                {
                    index++;
                }
                else if (current == '\'')
                {
                    quote = '\0';
                }

                continue;
            }

            if (current is '"' or '\'')
            {
                quote = current;
            }
            else if (current == ':')
            {
                return index;
            }
            else if (current is ',' or '}' or ']')
            {
                return -1;
            }
        }

        return -1;
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
        for (int index = 0; index < source.Count;)
        {
            if ((index & CancellationCheckLineMask) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            string line = source[index];
            if (!TryGetRootKey(line, out string key) || !IsManagedRootKey(key))
            {
                result.Add(line);
                index++;
                continue;
            }

            YamlNodeRange range = FindYamlNodeRange(source, index, key, cancellationToken);
            index = range.LastLine + 1;
        }

        return result;
    }

    private static YamlNodeRange FindYamlNodeRange(
        IReadOnlyList<string> source,
        int firstLine,
        string key,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        int nodeIndent = GetIndent(source[firstLine]);
        string value = GetRootValue(source[firstLine]);
        if (value.StartsWith('&'))
        {
            throw new InvalidDataException(
                $"受管 YAML 字段 {key} 定义了 anchor，无法安全替换；请移除该 anchor 后重试。");
        }

        if (value.StartsWith('|') || value.StartsWith('>'))
        {
            if (!IsBlockScalarHeader(value))
            {
                throw new InvalidDataException($"受管 YAML 字段 {key} 的块标量标记无法识别；已保留现有运行配置。");
            }

            return new YamlNodeRange(firstLine, FindIndentedNodeEnd(source, firstLine, nodeIndent, cancellationToken));
        }

        if (value.StartsWith('\'') || value.StartsWith('"'))
        {
            int lastLine = FindQuotedScalarEnd(
                source,
                firstLine,
                nodeIndent,
                value[0],
                cancellationToken);
            return new YamlNodeRange(
                firstLine,
                FindIndentedNodeEnd(source, lastLine, nodeIndent, cancellationToken));
        }

        if ((value.StartsWith('{') || value.StartsWith('['))
            && !IsFlowCollectionBalanced(value))
        {
            throw new InvalidDataException(
                $"受管 YAML 字段 {key} 使用不平衡的 flow 集合，无法安全替换；已保留现有运行配置。");
        }

        return new YamlNodeRange(firstLine, FindIndentedNodeEnd(source, firstLine, nodeIndent, cancellationToken));
    }

    private static int FindIndentedNodeEnd(
        IReadOnlyList<string> source,
        int firstLine,
        int parentIndent,
        CancellationToken cancellationToken)
    {
        int lastNodeLine = firstLine;
        for (int index = firstLine + 1; index < source.Count; index++)
        {
            if ((index & CancellationCheckLineMask) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            string line = source[index];
            if (string.IsNullOrWhiteSpace(line) || GetIndent(line) > parentIndent)
            {
                lastNodeLine = index;
                continue;
            }

            break;
        }

        return lastNodeLine;
    }

    private static bool IsIndentedYamlContinuation(string line, int parentIndent) =>
        string.IsNullOrWhiteSpace(line) || GetIndent(line) > parentIndent;

    private static string GetRootValue(string line)
    {
        int separator = FindYamlKeySeparator(line);
        if (separator < 0)
        {
            return string.Empty;
        }

        string value = line[(separator + 1)..].Trim();
        int commentStart = FindInlineCommentStart(value);
        if (commentStart >= 0)
        {
            value = value[..commentStart].TrimEnd();
        }

        return value.Trim();
    }

    internal static int FindQuotedScalarEnd(
        IReadOnlyList<string> source,
        int firstLine,
        int parentIndent,
        char quote,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        bool escaped = false;
        int scannedCharacters = 0;
        for (int lineIndex = firstLine; lineIndex < source.Count; lineIndex++)
        {
            if ((lineIndex & CancellationCheckLineMask) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
            string line = source[lineIndex];
            int scanStart;
            if (lineIndex == firstLine)
            {
                int separator = FindYamlKeySeparator(line);
                scanStart = separator + 1;
                while (scanStart < line.Length && char.IsWhiteSpace(line[scanStart]))
                {
                    scanStart++;
                }

                if (scanStart >= line.Length || line[scanStart] != quote)
                {
                    throw new InvalidDataException("受管 YAML 引号标量无法安全定位；已保留现有运行配置。");
                }

                scanStart++;
            }
            else
            {
                if (!IsIndentedYamlContinuation(line, parentIndent))
                {
                    throw new InvalidDataException(
                        "受管 YAML 多行引号值未闭合；已保留现有运行配置。");
                }

                scanStart = GetIndent(line);
            }

            for (int index = scanStart; index < line.Length; index++)
            {
                if ((++scannedCharacters & CancellationCheckCharacterMask) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                char current = line[index];
                if (quote == '"')
                {
                    if (escaped)
                    {
                        escaped = false;
                    }
                    else if (current == '\\')
                    {
                        escaped = true;
                    }
                    else if (current == '"')
                    {
                        return lineIndex;
                    }

                    continue;
                }

                if (current == '\'' && index + 1 < line.Length && line[index + 1] == '\'')
                {
                    index++;
                    scannedCharacters++;
                }
                else if (current == '\'')
                {
                    return lineIndex;
                }
            }

            // In YAML, a trailing backslash escapes the line break itself.
            escaped = false;
        }

        throw new InvalidDataException("受管 YAML 多行引号值未闭合；已保留现有运行配置。");
    }

    private static bool IsBlockScalarHeader(string value)
    {
        if (value.Length == 0 || value[0] is not ('|' or '>'))
        {
            return false;
        }

        bool hasChomping = false;
        bool hasIndent = false;
        foreach (char indicator in value.AsSpan(1).Trim())
        {
            if (indicator is '+' or '-')
            {
                if (hasChomping)
                {
                    return false;
                }

                hasChomping = true;
            }
            else if (indicator is >= '1' and <= '9')
            {
                if (hasIndent)
                {
                    return false;
                }

                hasIndent = true;
            }
            else
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsFlowCollectionBalanced(string value)
    {
        Stack<char> closing = new();
        char quote = '\0';
        bool escaped = false;
        for (int index = 0; index < value.Length; index++)
        {
            char current = value[index];
            if (quote == '"')
            {
                if (escaped)
                {
                    escaped = false;
                }
                else if (current == '\\')
                {
                    escaped = true;
                }
                else if (current == '"')
                {
                    quote = '\0';
                }

                continue;
            }

            if (quote == '\'')
            {
                if (current == '\'' && index + 1 < value.Length && value[index + 1] == '\'')
                {
                    index++;
                }
                else if (current == '\'')
                {
                    quote = '\0';
                }

                continue;
            }

            if (current is '"' or '\'')
            {
                quote = current;
            }
            else if (current == '{')
            {
                if (closing.Count >= MaximumFlowCollectionDepth)
                {
                    return false;
                }
                closing.Push('}');
            }
            else if (current == '[')
            {
                if (closing.Count >= MaximumFlowCollectionDepth)
                {
                    return false;
                }
                closing.Push(']');
            }
            else if (current is '}' or ']')
            {
                if (closing.Count == 0 || closing.Pop() != current)
                {
                    return false;
                }

                if (closing.Count == 0 && index != value.Length - 1)
                {
                    return false;
                }
            }
        }

        return quote == '\0' && closing.Count == 0;
    }

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

    private static int GetIndent(string line) => line.Length - line.TrimStart().Length;

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

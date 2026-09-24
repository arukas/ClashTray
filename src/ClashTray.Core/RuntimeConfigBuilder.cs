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
        List<string> filtered = RemoveManagedRootEntries(withTunOverride);
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

    private static List<string> ApplyTunOverride(string[] source, bool enabled, string stack)
    {
        string? managedStack = string.Equals(stack, "configuration", StringComparison.OrdinalIgnoreCase)
            ? null
            : stack.Trim().ToLowerInvariant();
        List<string> result = new List<string>(source.Length + 4);
        bool inTunBlock = false;
        int tunIndent = -1;
        int tunChildIndent = -1;
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
                result.Add(ReplaceTunPropertyLine(line, "enable", enabled ? "true" : "false"));
                tunHasEnable = true;
                continue;
            }

            if (inTunBlock
                && managedStack is not null
                && IsTunPropertyLine(line, tunChildIndent, "stack"))
            {
                result.Add(ReplaceTunPropertyLine(line, "stack", managedStack));
                tunHasStack = true;
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

    private static List<string> RemoveManagedRootEntries(List<string> source)
    {
        List<string> result = new(source.Count + 12);
        for (int index = 0; index < source.Count;)
        {
            string line = source[index];
            if (!TryGetRootKey(line, out string key) || !IsManagedRootKey(key))
            {
                result.Add(line);
                index++;
                continue;
            }

            int finalNodeLine = FindManagedNodeEnd(source, index, key);
            index = finalNodeLine + 1;
        }

        return result;
    }

    private static int FindManagedNodeEnd(List<string> source, int rootLineIndex, string key)
    {
        string value = GetRootValue(source[rootLineIndex]);
        if (value.StartsWith('&'))
        {
            throw new InvalidDataException(
                $"受管 YAML 字段 {key} 定义了 anchor，无法保证删除其引用；请移除该 anchor 后重试。");
        }

        if (value.StartsWith('|') || value.StartsWith('>'))
        {
            if (!IsBlockScalarHeader(value))
            {
                throw new InvalidDataException($"受管 YAML 字段 {key} 的块标量标记无法识别，已保留现有运行配置。");
            }

            return FindIndentedNodeEnd(source, rootLineIndex);
        }

        if (value.StartsWith('\'') || value.StartsWith('"'))
        {
            char quote = value[0];
            int lastLine = rootLineIndex;
            string scalar = value;
            while (FindQuotedScalarEnd(scalar, quote) < 0)
            {
                int nextLine = lastLine + 1;
                if (nextLine >= source.Count
                    || !IsIndentedYamlContinuation(source[nextLine]))
                {
                    throw new InvalidDataException(
                        $"受管 YAML 字段 {key} 的多行引号值未闭合；已保留现有运行配置。");
                }

                scalar += "\n" + source[nextLine].TrimStart();
                lastLine = nextLine;
            }

            return Math.Max(lastLine, FindIndentedNodeEnd(source, lastLine));
        }

        if ((value.StartsWith('{') || value.StartsWith('['))
            && !IsFlowCollectionBalanced(value))
        {
            throw new InvalidDataException(
                $"受管 YAML 字段 {key} 使用不平衡的 flow 集合，无法安全替换；已保留现有运行配置。");
        }

        return FindIndentedNodeEnd(source, rootLineIndex);
    }

    private static int FindIndentedNodeEnd(List<string> source, int rootLineIndex)
    {
        int lastNodeLine = rootLineIndex;
        for (int index = rootLineIndex + 1; index < source.Count; index++)
        {
            string line = source[index];
            if (string.IsNullOrWhiteSpace(line) || GetIndent(line) > 0)
            {
                lastNodeLine = index;
                continue;
            }

            break;
        }

        return lastNodeLine;
    }

    private static bool IsIndentedYamlContinuation(string line) =>
        string.IsNullOrWhiteSpace(line) || GetIndent(line) > 0;

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

    private static int FindQuotedScalarEnd(string value, char quote)
    {
        bool escaped = false;
        for (int index = 1; index < value.Length; index++)
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
                    return index;
                }

                continue;
            }

            if (current == '\'' && index + 1 < value.Length && value[index + 1] == '\'')
            {
                index++;
            }
            else if (current == '\'')
            {
                return index;
            }
        }

        return -1;
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
                closing.Push('}');
            }
            else if (current == '[')
            {
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

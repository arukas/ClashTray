namespace ClashTray.Core;

internal readonly record struct YamlDocumentScope(int RootIndent, int EndMarkerLine, bool HasExplicitStartMarker);
internal readonly record struct YamlNodeRange(int FirstLine, int LastLine);

internal static class YamlStructure
{
    private const int CancellationCheckLineMask = 0x3F;
    private const int CancellationCheckCharacterMask = 0xFFF;
    private const int MaximumFlowCollectionDepth = 128;
    internal static YamlDocumentScope ValidateYamlDocumentScope(
        string[] source,
        CancellationToken cancellationToken,
        bool allowListenerSequence = false)
    {
        bool hasExplicitStartMarker = false;
        bool hasDocumentContent = false;
        bool ended = false;
        int endMarkerLine = -1;
        string lastRootKey = string.Empty;
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

            if (!TryGetRootKey(line, out string rootKey))
            {
                if (allowListenerSequence && lastRootKey == "listeners" && IsSequenceEntry(line)) { continue; }
                throw new InvalidDataException(
                    "The managed runtime configuration builder requires a single block-mapping YAML document.");
            }

            hasDocumentContent = true;
            lastRootKey = rootKey;
        }

        return new YamlDocumentScope(RootIndent: 0, endMarkerLine, hasExplicitStartMarker);
    }

    internal static bool IsYamlDocumentStartMarker(string line) =>
        IsYamlDocumentMarker(line, "---");

    internal static bool IsYamlDocumentEndMarker(string line) =>
        IsYamlDocumentMarker(line, "...");

    internal static bool IsYamlDocumentMarker(string line, string marker)
    {
        if (line.Length < marker.Length
            || !line.AsSpan().StartsWith(marker, StringComparison.Ordinal))
        {
            return false;
        }

        return line.Length == marker.Length || char.IsWhiteSpace(line[marker.Length]);
    }


    internal static int FindInlineEntryEnd(string value, int start)
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

    internal static int FindMatchingFlowMapEnd(string value, int start)
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

    internal static int FindInlineCommentStart(string line)
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

    internal static bool TryGetRootKey(string line, out string key)
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

        return YamlMappingKeyReader.TryDecode(trimmed[..separator].Trim(), out key);
    }

    internal static int FindYamlKeySeparator(string value) => YamlMappingKeyReader.FindSeparator(value);


    internal static YamlNodeRange FindYamlNodeRange(
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

    internal static int FindIndentedNodeEnd(
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

    internal static bool IsIndentedYamlContinuation(string line, int parentIndent) =>
        string.IsNullOrWhiteSpace(line) || GetIndent(line) > parentIndent;

    internal static string GetRootValue(string line)
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

    internal static bool IsBlockScalarHeader(string value)
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

    internal static bool IsFlowCollectionBalanced(string value)
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


    internal static int GetIndent(string line) => line.Length - line.TrimStart().Length;
    internal static string StripComment(string line)
    {
        int comment = FindInlineCommentStart(line);
        return comment < 0 ? line : line[..comment].TrimEnd();
    }

    internal static bool IsSequenceEntry(string line) => line.StartsWith("- ", StringComparison.Ordinal) || line.Trim() == "-";
    internal static bool IsUnresolvedRootMapping(string line)
    {
        if (line.Length == 0 || char.IsWhiteSpace(line[0]) || line[0] == '#') { return false; }
        string content = StripComment(line).TrimStart();
        return content.Length > 0 && !IsYamlDocumentStartMarker(content) && !IsYamlDocumentEndMarker(content);
    }
}

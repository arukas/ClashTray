using System.Globalization;
using System.Text;
using System.Text.Json;

namespace ClashTray.Core;

/// <summary>Reads single-line scalar mapping keys; unsupported YAML is never guessed.</summary>
internal static class YamlMappingKeyReader
{
    public static bool TryRead(string line, out string key, out int separator)
    {
        separator = FindSeparator(line);
        key = string.Empty;
        return separator > 0 && TryDecode(line[..separator].Trim(), out key);
    }

    public static bool TryDecode(string rawKey, out string key)
    {
        key = string.Empty;
        if (rawKey.Length == 0)
        {
            return false;
        }

        if (rawKey[0] == '\'')
        {
            if (rawKey.Length < 2 || rawKey[^1] != '\'')
            {
                return false;
            }

            string content = rawKey[1..^1];
            for (int index = 0; index < content.Length; index++)
            {
                if (content[index] == '\'')
                {
                    if (index + 1 >= content.Length || content[++index] != '\'')
                    {
                        return false;
                    }
                }
            }

            key = content.Replace("''", "'", StringComparison.Ordinal);
        }
        else if (rawKey[0] == '"')
        {
            if (rawKey.Length < 2 || rawKey[^1] != '"')
            {
                return false;
            }

            // JSON handles the common YAML double-quoted escapes, including
            // \u. Normalize YAML's \x and \U hexadecimal forms first. Other
            // YAML-only escapes are deliberately unsupported and fail closed.
            StringBuilder normalized = new(rawKey.Length);
            for (int index = 0; index < rawKey.Length; index++)
            {
                char current = rawKey[index];
                if (current != '\\' || index + 1 >= rawKey.Length)
                {
                    normalized.Append(current);
                    continue;
                }

                char escape = rawKey[++index];
                if (escape is not ('x' or 'U'))
                {
                    normalized.Append('\\').Append(escape);
                    continue;
                }

                int digits = escape == 'x' ? 2 : 8;
                if (index + digits >= rawKey.Length
                    || !int.TryParse(rawKey.AsSpan(index + 1, digits), NumberStyles.AllowHexSpecifier,
                        CultureInfo.InvariantCulture, out int codePoint)
                    || !Rune.IsValid(codePoint))
                {
                    return false;
                }

                foreach (char character in char.ConvertFromUtf32(codePoint))
                {
                    normalized.Append("\\u").Append(((int)character).ToString("X4", CultureInfo.InvariantCulture));
                }

                index += digits;
            }

            try
            {
                key = JsonSerializer.Deserialize<string>(normalized.ToString()) ?? string.Empty;
            }
            catch (JsonException)
            {
                return false;
            }
        }
        else
        {
            if (rawKey[0] is '*' or '&' or '!' or '?' or '[' or ']' or '{' or '}' or '|' or '>'
                || rawKey.StartsWith("- ", StringComparison.Ordinal) || rawKey == "-"
                || rawKey.Contains('"', StringComparison.Ordinal) || rawKey.Contains('\'', StringComparison.Ordinal))
            {
                return false;
            }

            key = rawKey;
        }

        return key.Length > 0 && !key.Any(char.IsControl);
    }

    public static int FindSeparator(string value)
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
            else if (current is ',' or '}' or ']' or '#')
            {
                return -1;
            }
        }

        return -1;
    }
}

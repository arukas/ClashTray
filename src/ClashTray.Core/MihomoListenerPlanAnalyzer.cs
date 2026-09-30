using System.Globalization;
using System.Net;
using System.Net.Sockets;
using ClashTray.Contracts;

namespace ClashTray.Core;

internal sealed record MihomoListenerPlan(
    IReadOnlyList<LocalPortBinding> Bindings,
    bool IsComplete,
    string? Warning)
{
    public IReadOnlyList<RuntimeListenerBinding> ToContractBindings() => Bindings
        .Select(binding => new RuntimeListenerBinding(
            binding.Name,
            binding.Address.ToString(),
            binding.Port,
            binding.Transport == PortTransport.Tcp
                ? RuntimeListenerTransport.Tcp
                : RuntimeListenerTransport.Udp))
        .ToArray();
}

/// <summary>
/// Reads only the local listener forms that the current block-YAML converter
/// can identify without changing the user's configuration. Unknown forms are
/// preserved and surfaced as an incomplete port-check plan.
/// </summary>
internal static class MihomoListenerPlanAnalyzer
{
    public static async Task<MihomoListenerPlan> AnalyzeFileAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        FileInfo info = new(path);
        if (!info.Exists || info.Length > RuntimeConfigBuilder.MaximumInputBytes)
        {
            throw new InvalidDataException("The effective Mihomo configuration is missing or exceeds its size limit.");
        }

        string[] lines = await File.ReadAllLinesAsync(path, cancellationToken).ConfigureAwait(false);
        if (lines.Length > RuntimeConfigBuilder.MaximumInputLines
            || lines.Any(line => line.Length > RuntimeConfigBuilder.MaximumLineCharacters))
        {
            throw new InvalidDataException("The effective Mihomo configuration exceeds its line limits.");
        }

        return AnalyzeLines(lines, cancellationToken);
    }

    internal static MihomoListenerPlan AnalyzeLines(
        IReadOnlyList<string> lines,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lines);
        List<LocalPortBinding> bindings = [];
        List<string> limitations = [];
        HashSet<string> seenSections = new(StringComparer.OrdinalIgnoreCase);
        for (int index = 0; index < lines.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryReadRootEntry(lines[index], out string key, out string value)
                || key is not ("dns" or "listeners"))
            {
                continue;
            }

            if (!seenSections.Add(key))
            {
                limitations.Add(key == "dns"
                    ? "多个 DNS 配置段"
                    : "多个自定义 listeners 配置段");
                continue;
            }

            int end = index + 1;
            while (end < lines.Count && !TryReadRootEntry(lines[end], out _, out _))
            {
                end++;
            }

            IReadOnlyList<string> section = lines.Skip(index + 1).Take(end - index - 1).ToArray();
            if (key == "dns")
            {
                AnalyzeDnsSection(value, section, bindings, limitations);
            }
            else
            {
                AnalyzeCustomListenersSection(value, section, bindings, limitations);
            }

            index = end - 1;
        }

        bool complete = limitations.Count == 0;
        string? warning = complete
            ? null
            : $"端口预检未覆盖 DNS/custom listener：{string.Join("、", limitations.Distinct().Take(4))}；原配置已保留，启动后的绑定故障仍由 Mihomo 与服务状态报告。";
        return new MihomoListenerPlan(bindings, complete, warning);
    }

    private static void AnalyzeDnsSection(
        string rootValue,
        IReadOnlyList<string> section,
        List<LocalPortBinding> bindings,
        List<string> limitations)
    {
        if (!IsEmptyYamlValue(rootValue))
        {
            limitations.Add("内联 DNS 配置");
            return;
        }

        bool? enabled = null;
        string? listen = null;
        foreach (string line in section)
        {
            if (!TryReadChildEntry(line, expectedIndent: 2, out string key, out string value))
            {
                continue;
            }

            if (key.Equals("enable", StringComparison.OrdinalIgnoreCase))
            {
                if (TryParseBoolean(value, out bool parsed))
                {
                    enabled = parsed;
                }
                else
                {
                    limitations.Add("无法静态读取 DNS enable");
                }
            }
            else if (key.Equals("listen", StringComparison.OrdinalIgnoreCase))
            {
                if (!string.IsNullOrWhiteSpace(value))
                {
                    listen = value;
                }
                else
                {
                    limitations.Add("无法静态读取 DNS listen");
                }
            }
        }

        if (enabled != true || string.IsNullOrWhiteSpace(listen))
        {
            return;
        }

        if (!TryParseListenEndpoint(listen, out IPAddress? address, out int port)
            || address is null)
        {
            limitations.Add("DNS listen 不是固定 IP 地址与端口");
            return;
        }

        bindings.Add(new LocalPortBinding("dns-tcp", address, port, PortTransport.Tcp));
        bindings.Add(new LocalPortBinding("dns-udp", address, port, PortTransport.Udp));
    }

    private static void AnalyzeCustomListenersSection(
        string rootValue,
        IReadOnlyList<string> section,
        List<LocalPortBinding> bindings,
        List<string> limitations)
    {
        if (!IsEmptyYamlValue(rootValue))
        {
            if (rootValue.Trim() is "[]" or "null" or "~")
            {
                return;
            }

            limitations.Add("内联自定义 listeners 配置");
            return;
        }

        List<Dictionary<string, string>> entries = [];
        Dictionary<string, string>? current = null;
        int sequenceIndent = -1;
        foreach (string line in section)
        {
            string content = StripYamlComment(line);
            if (string.IsNullOrWhiteSpace(content))
            {
                continue;
            }

            int indent = GetIndent(content);
            string trimmed = content[indent..].TrimEnd();
            if (trimmed.StartsWith('-')
                && (trimmed.Length == 1 || char.IsWhiteSpace(trimmed[1])))
            {
                sequenceIndent = sequenceIndent < 0 ? indent : sequenceIndent;
                if (indent != sequenceIndent)
                {
                    limitations.Add("无法静态读取自定义 listener 序列");
                    current = null;
                    continue;
                }

                current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                entries.Add(current);
                string inline = trimmed.Length > 1 ? trimmed[1..].Trim() : string.Empty;
                if (inline.Length > 0 && TrySplitMapping(inline, out string inlineKey, out string inlineValue))
                {
                    current[inlineKey] = inlineValue;
                }
                else if (inline.Length > 0)
                {
                    limitations.Add("内联自定义 listener 项");
                }

                continue;
            }

            if (current is null)
            {
                limitations.Add("无法静态读取自定义 listeners 结构");
                continue;
            }

            if (TrySplitMapping(trimmed, out string key, out string value))
            {
                current[key] = value;
            }
            else if (indent > sequenceIndent)
            {
                limitations.Add("无法静态读取自定义 listener 字段");
            }
        }

        for (int index = 0; index < entries.Count; index++)
        {
            Dictionary<string, string> entry = entries[index];
            if (entry.TryGetValue("disabled", out string? disabled))
            {
                if (!TryParseBoolean(disabled, out bool isDisabled))
                {
                    limitations.Add("无法静态读取自定义 listener disabled 状态");
                    continue;
                }

                if (isDisabled)
                {
                    continue;
                }
            }

            if (!entry.TryGetValue("type", out string? rawType)
                || !TryParseScalar(rawType, out string type)
                || !entry.TryGetValue("listen", out string? rawListen)
                || !TryParseScalar(rawListen, out string listen)
                || !entry.TryGetValue("port", out string? rawPort)
                || !TryParseScalar(rawPort, out string portText)
                || !int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out int port)
                || port is < 1 or > 65535
                || !TryParseListenAddress(listen, out IPAddress? address)
                || address is null)
            {
                limitations.Add("自定义 listener 缺少可确认的固定 IP、类型或端口");
                continue;
            }

            bool udp = false;
            if (entry.TryGetValue("udp", out string? rawUdp))
            {
                if (!TryParseBoolean(rawUdp, out udp))
                {
                    limitations.Add("无法静态读取自定义 listener udp 状态");
                }
            }
            string name = $"custom-listener-{index + 1}";
            switch (type.ToUpperInvariant())
            {
                case "HTTP":
                case "REDIR":
                    bindings.Add(new LocalPortBinding(name, address, port, PortTransport.Tcp));
                    if (udp)
                    {
                        limitations.Add("HTTP/redirect listener 的 udp 选项");
                    }

                    break;
                case "SOCKS":
                case "MIXED":
                    bindings.Add(new LocalPortBinding(name, address, port, PortTransport.Tcp));
                    if (udp)
                    {
                        bindings.Add(new LocalPortBinding($"{name}-udp", address, port, PortTransport.Udp));
                    }

                    break;
                case "TPROXY":
                    bindings.Add(new LocalPortBinding(name, address, port, PortTransport.Udp));
                    break;
                default:
                    limitations.Add("未覆盖的自定义 listener 类型");
                    break;
            }
        }
    }

    private static bool TryReadRootEntry(string line, out string key, out string value)
    {
        key = string.Empty;
        value = string.Empty;
        if (line.Length == 0 || char.IsWhiteSpace(line[0]) || line[0] == '#')
        {
            return false;
        }

        return TrySplitMapping(StripYamlComment(line), out key, out value);
    }

    private static bool TryReadChildEntry(string line, int expectedIndent, out string key, out string value)
    {
        key = string.Empty;
        value = string.Empty;
        string content = StripYamlComment(line);
        if (string.IsNullOrWhiteSpace(content) || GetIndent(content) != expectedIndent)
        {
            return false;
        }

        return TrySplitMapping(content[expectedIndent..], out key, out value);
    }

    private static bool TrySplitMapping(string line, out string key, out string value)
    {
        key = string.Empty;
        value = string.Empty;
        int colon = line.IndexOf(':', StringComparison.Ordinal);
        if (colon <= 0)
        {
            return false;
        }

        key = line[..colon].Trim();
        value = line[(colon + 1)..].Trim();
        return key.Length > 0 && key.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');
    }

    private static bool TryParseScalar(string value, out string scalar)
    {
        scalar = string.Empty;
        value = value.Trim();
        if (value.Length == 0 || value.StartsWith('&') || value.StartsWith('*') || value.StartsWith('!'))
        {
            return false;
        }

        if (value[0] == '\'' && value[^1] == '\'' && value.Length >= 2)
        {
            scalar = value[1..^1].Replace("''", "'", StringComparison.Ordinal);
            return true;
        }

        if (value[0] == '"' && value[^1] == '"' && value.Length >= 2)
        {
            try
            {
                scalar = System.Text.Json.JsonSerializer.Deserialize<string>(value) ?? string.Empty;
                return scalar.Length > 0;
            }
            catch (System.Text.Json.JsonException)
            {
                return false;
            }
        }

        if (value.Contains('{', StringComparison.Ordinal)
            || value.Contains('[', StringComparison.Ordinal)
            || value.Contains('|', StringComparison.Ordinal)
            || value.Contains('>', StringComparison.Ordinal))
        {
            return false;
        }

        scalar = value.Trim();
        return scalar.Length > 0;
    }

    private static bool TryParseBoolean(string value, out bool result)
    {
        result = false;
        return TryParseScalar(value, out string scalar)
            && bool.TryParse(scalar, out result);
    }

    internal static bool TryParseListenEndpoint(string value, out IPAddress? address, out int port)
    {
        address = null;
        port = 0;
        if (!TryParseScalar(value, out string scalar))
        {
            return false;
        }

        string host;
        string portText;
        if (scalar.StartsWith('['))
        {
            int closingBracket = scalar.IndexOf(']', StringComparison.Ordinal);
            if (closingBracket <= 1 || closingBracket + 2 >= scalar.Length || scalar[closingBracket + 1] != ':')
            {
                return false;
            }

            host = scalar[1..closingBracket];
            portText = scalar[(closingBracket + 2)..];
        }
        else
        {
            int colon = scalar.AsSpan().LastIndexOf(':');
            if (colon <= 0 || scalar.AsSpan(0, colon).Contains(':'))
            {
                return false;
            }

            host = scalar[..colon];
            portText = scalar[(colon + 1)..];
        }

        return IPAddress.TryParse(host, out address)
            && int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out port)
            && port is >= 1 and <= 65535;
    }

    private static bool TryParseListenAddress(string value, out IPAddress? address)
    {
        address = null;
        value = value.Trim();
        if (value.StartsWith('[') && value.EndsWith(']'))
        {
            value = value[1..^1];
        }

        return IPAddress.TryParse(value, out address)
            && address.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6;
    }

    private static bool IsEmptyYamlValue(string value) =>
        value.Length == 0 || value is "null" or "~";

    private static string StripYamlComment(string line)
    {
        bool singleQuoted = false;
        bool doubleQuoted = false;
        bool escaped = false;
        for (int index = 0; index < line.Length; index++)
        {
            char current = line[index];
            if (escaped)
            {
                escaped = false;
                continue;
            }

            if (doubleQuoted && current == '\\')
            {
                escaped = true;
                continue;
            }

            if (!doubleQuoted && current == '\'')
            {
                singleQuoted = !singleQuoted;
            }
            else if (!singleQuoted && current == '"')
            {
                doubleQuoted = !doubleQuoted;
            }
            else if (!singleQuoted && !doubleQuoted && current == '#'
                && (index == 0 || char.IsWhiteSpace(line[index - 1])))
            {
                return line[..index].TrimEnd();
            }
        }

        return line;
    }

    private static int GetIndent(string line)
    {
        int index = 0;
        while (index < line.Length && line[index] == ' ')
        {
            index++;
        }

        return index;
    }
}

using System.Text.Json;
using ClashTray.Contracts;

namespace ClashTray.Core;

public sealed record EndpointHandshakeResult(
    EndpointSessionState State,
    string? Version,
    EndpointCapability Capabilities,
    string? ErrorMessage)
{
    public bool IsCompatible => State == EndpointSessionState.Connected;
}

/// <summary>
/// Validates a Mihomo /version response without performing I/O.
/// </summary>
public static class EndpointHandshakeValidator
{
    private const int MaxVersionCharacters = 128;

    public static EndpointHandshakeResult Validate(JsonDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("version", out JsonElement versionElement)
            || versionElement.ValueKind != JsonValueKind.String)
        {
            return Incompatible("Controller 的 /version 响应缺少有效的 version 字段。");
        }

        string version = versionElement.GetString()?.Trim() ?? string.Empty;
        if (version.Length == 0 || version.Length > MaxVersionCharacters)
        {
            return Incompatible("Controller 的版本信息无效。");
        }

        return new EndpointHandshakeResult(
            EndpointSessionState.Connected,
            version,
            EndpointCapabilityDefaults.Remote,
            ErrorMessage: null);
    }

    private static EndpointHandshakeResult Incompatible(string message) =>
        new(
            EndpointSessionState.Incompatible,
            Version: null,
            EndpointCapability.None,
            ErrorSanitizer.Sanitize(message));
}

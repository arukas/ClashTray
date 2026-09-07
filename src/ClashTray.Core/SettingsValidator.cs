using ClashTray.Contracts;

namespace ClashTray.Core;

internal static class SettingsValidator
{
    private static readonly string[] AllowedLogLevels = ["info", "warning", "error", "debug"];
    private static readonly string[] AllowedThemes = ["system", "light", "dark"];

    public static void Validate(AppSettings settings)
    {
        var ports = new[] { settings.HttpPort, settings.SocksPort, settings.MixedPort, settings.ControllerPort };
        if (ports.Any(port => port is < 1 or > 65535))
        {
            throw new ArgumentOutOfRangeException(nameof(settings), "端口必须在 1 到 65535 之间。");
        }

        if (ports.Distinct().Count() != ports.Length)
        {
            throw new ArgumentException("HTTP、SOCKS、Mixed 和控制器端口不能重复。", nameof(settings));
        }

        if (settings.SubscriptionRefreshHours is < 1 or > 168)
        {
            throw new ArgumentOutOfRangeException(nameof(settings), "订阅刷新间隔必须在 1 到 168 小时之间。");
        }

        if (!AllowedLogLevels.Contains(settings.LogLevel?.Trim(), StringComparer.OrdinalIgnoreCase))
        {
            throw new ArgumentException("日志级别必须是 info、warning、error 或 debug。", nameof(settings));
        }

        if (!AllowedThemes.Contains(settings.Theme?.Trim(), StringComparer.OrdinalIgnoreCase))
        {
            throw new ArgumentException("主题必须是 system、light 或 dark。", nameof(settings));
        }

        if (settings.BypassList is null
            || settings.BypassList.Length > 4096
            || settings.BypassList.Contains('\r')
            || settings.BypassList.Contains('\n'))
        {
            throw new ArgumentException("系统代理绕过列表包含无效内容。", nameof(settings));
        }
    }
}

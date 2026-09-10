using System.Text.Json;

namespace ClashTray.Core;

public static class BundledMihomo
{
    public static string Version { get; } = ReadVersion();

    // Matches DefaultRawConfig.GlobalUA in Mihomo v1.19.30 config/config.go.
    public static string UserAgent { get; } = $"clash.meta/{Version}";

    private static string ReadVersion()
    {
        using Stream stream = typeof(BundledMihomo).Assembly.GetManifestResourceStream("ClashTray.MihomoRelease.json")
            ?? throw new InvalidOperationException("Missing bundled Mihomo release metadata.");
        using JsonDocument document = JsonDocument.Parse(stream);
        return document.RootElement.GetProperty("version").GetString()
            ?? throw new InvalidOperationException("Missing bundled Mihomo version.");
    }
}

using System.Text;
using ClashTray.Contracts;

namespace ClashTray.Core;

public sealed class RuntimeConfigBuilder
{
    private readonly ControllerSecretStore _secretStore;

    public RuntimeConfigBuilder(ControllerSecretStore secretStore)
    {
        _secretStore = secretStore;
    }

    public async Task<string> BuildAsync(
        string sourcePath,
        string destinationPath,
        AppSettings settings,
        CancellationToken cancellationToken = default)
    {
        SettingsValidator.Validate(settings);
        var source = await File.ReadAllLinesAsync(sourcePath, cancellationToken);
        var filtered = source.Where(line => !IsManagedLine(line)).ToList();
        filtered.Add(string.Empty);
        filtered.Add($"external-controller: 127.0.0.1:{settings.ControllerPort}");
        filtered.Add($"secret: {_secretStore.GetOrCreate()}");
        filtered.Add($"allow-lan: {(settings.AllowLan ? "true" : "false")}");
        filtered.Add($"ipv6: {(settings.Ipv6 ? "true" : "false")}");
        filtered.Add($"tcp-concurrent: {(settings.TcpConcurrent ? "true" : "false")}");
        filtered.Add($"log-level: {settings.LogLevel.Trim().ToLowerInvariant()}");
        filtered.Add($"port: {settings.HttpPort}");
        filtered.Add($"mixed-port: {settings.MixedPort}");
        filtered.Add($"socks-port: {settings.SocksPort}");

        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        var tempPath = destinationPath + ".tmp";
        await File.WriteAllLinesAsync(tempPath, filtered, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), cancellationToken);
        File.Move(tempPath, destinationPath, overwrite: true);
        WindowsPathSecurity.ProtectRuntimeFile(destinationPath);
        return destinationPath;
    }

    private static bool IsManagedLine(string line)
    {
        if (line.Length > 0 && char.IsWhiteSpace(line[0]))
        {
            return false;
        }

        var trimmed = line.TrimStart();
        return trimmed.StartsWith("external-controller:", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("secret:", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("allow-lan:", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("ipv6:", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("tcp-concurrent:", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("log-level:", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("port:", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("mixed-port:", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("socks-port:", StringComparison.OrdinalIgnoreCase);
    }
}

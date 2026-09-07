using System.Text.Json;
using ClashTray.Contracts;
using Microsoft.Win32;

namespace ClashTray.Core;

public sealed class SystemProxyManager
{
    private const string InternetSettingsPath = "Software\\Microsoft\\Windows\\CurrentVersion\\Internet Settings";
    private readonly AppPaths _paths;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public SystemProxyManager(AppPaths paths)
    {
        _paths = paths;
        _paths.EnsureDirectories();
    }

    public SystemProxyState State { get; private set; } = SystemProxyState.Off;

    public static bool IsEnabled => ReadDword("ProxyEnable") == 1;

    public SystemProxyState DetectState()
    {
        var current = ReadCurrentState();
        var ownership = ReadOwnership();
        if (ownership is not null && IsOwnedByClashTray(current, ownership))
        {
            State = SystemProxyState.On;
        }
        else if (File.Exists(_paths.ProxyBackupFile))
        {
            State = SystemProxyState.RestoreRequired;
        }
        else
        {
            State = SystemProxyState.Off;
        }

        return State;
    }

    public async Task EnableAsync(int port, string bypassList, CancellationToken cancellationToken = default)
    {
        State = SystemProxyState.Enabling;
        try
        {
            var current = ReadCurrentState();
            var existingOwnership = ReadOwnership();
            if (existingOwnership is not null && !IsOwnedByClashTray(current, existingOwnership))
            {
                State = SystemProxyState.RestoreRequired;
                throw new InvalidOperationException("系统代理已被其他程序修改，请先确认并恢复原设置。");
            }

            if (!File.Exists(_paths.ProxyBackupFile))
            {
                await File.WriteAllTextAsync(_paths.ProxyBackupFile, JsonSerializer.Serialize(current, _jsonOptions), cancellationToken);
            }

            using var key = Registry.CurrentUser.OpenSubKey(InternetSettingsPath, writable: true)
                ?? throw new InvalidOperationException("Windows Internet Settings registry key is unavailable.");
            key.SetValue("ProxyEnable", 1, RegistryValueKind.DWord);
            key.SetValue("ProxyServer", $"127.0.0.1:{port}", RegistryValueKind.String);
            key.SetValue("ProxyOverride", bypassList, RegistryValueKind.String);
            await File.WriteAllTextAsync(
                _paths.ProxyOwnershipFile,
                JsonSerializer.Serialize(new ProxyOwnershipState($"127.0.0.1:{port}", bypassList), _jsonOptions),
                cancellationToken);
            InternetSettingsNotifier.Notify();
            State = SystemProxyState.On;
        }
        catch
        {
            if (State is not SystemProxyState.RestoreRequired)
            {
                State = SystemProxyState.Failed;
            }

            throw;
        }
    }

    public async Task DisableAsync(CancellationToken cancellationToken = default)
    {
        State = SystemProxyState.Disabling;
        try
        {
            var current = ReadCurrentState();
            var ownership = ReadOwnership();
            if (File.Exists(_paths.ProxyBackupFile))
            {
                var backup = JsonSerializer.Deserialize<ProxyRegistryState>(await File.ReadAllTextAsync(_paths.ProxyBackupFile, cancellationToken), _jsonOptions);
                if (backup is not null && ownership is not null && IsOwnedByClashTray(current, ownership))
                {
                    WriteState(backup);
                    File.Delete(_paths.ProxyBackupFile);
                    File.Delete(_paths.ProxyOwnershipFile);
                }
                else if (backup is not null)
                {
                    State = SystemProxyState.RestoreRequired;
                    return;
                }
            }
            else if (ownership is not null && IsOwnedByClashTray(current, ownership))
            {
                current = current with { ProxyEnable = 0, ProxyServer = null, ProxyOverride = null };
                WriteState(current);
                File.Delete(_paths.ProxyOwnershipFile);
            }

            InternetSettingsNotifier.Notify();
            State = SystemProxyState.Off;
        }
        catch
        {
            State = SystemProxyState.Failed;
            throw;
        }
    }

    public static ProxyRegistryState ReadCurrentState()
    {
        using var key = Registry.CurrentUser.OpenSubKey(InternetSettingsPath, writable: false)
            ?? throw new InvalidOperationException("Windows Internet Settings registry key is unavailable.");
        return new ProxyRegistryState(
            GetValue(key, "ProxyEnable"),
            key.GetValue("ProxyServer") as string,
            key.GetValue("ProxyOverride") as string,
            key.GetValue("AutoConfigURL") as string,
            key.GetValue("AutoDetect") as int? ?? 0);
    }

    private static int ReadDword(string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(InternetSettingsPath, writable: false);
        return key is null ? 0 : GetValue(key, name);
    }

    private static int GetValue(RegistryKey key, string name) =>
        key.GetValue(name) is int value ? value : 0;

    private static void WriteState(ProxyRegistryState state)
    {
        using var key = Registry.CurrentUser.OpenSubKey(InternetSettingsPath, writable: true)
            ?? throw new InvalidOperationException("Windows Internet Settings registry key is unavailable.");
        key.SetValue("ProxyEnable", state.ProxyEnable, RegistryValueKind.DWord);
        SetOrDelete(key, "ProxyServer", state.ProxyServer);
        SetOrDelete(key, "ProxyOverride", state.ProxyOverride);
        SetOrDelete(key, "AutoConfigURL", state.AutoConfigUrl);
        key.SetValue("AutoDetect", state.AutoDetect, RegistryValueKind.DWord);
    }

    private static void SetOrDelete(RegistryKey key, string name, string? value)
    {
        if (value is null)
        {
            key.DeleteValue(name, throwOnMissingValue: false);
        }
        else
        {
            key.SetValue(name, value, RegistryValueKind.String);
        }
    }

    private ProxyOwnershipState? ReadOwnership()
    {
        if (!File.Exists(_paths.ProxyOwnershipFile))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<ProxyOwnershipState>(File.ReadAllText(_paths.ProxyOwnershipFile), _jsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool IsOwnedByClashTray(ProxyRegistryState state, ProxyOwnershipState ownership) =>
        state.ProxyEnable == 1
        && string.Equals(state.ProxyServer, ownership.ProxyServer, StringComparison.OrdinalIgnoreCase)
        && string.Equals(state.ProxyOverride ?? string.Empty, ownership.ProxyOverride ?? string.Empty, StringComparison.Ordinal);
}

public sealed record ProxyRegistryState(
    int ProxyEnable,
    string? ProxyServer,
    string? ProxyOverride,
    string? AutoConfigUrl,
    int AutoDetect);

public sealed record ProxyOwnershipState(string ProxyServer, string? ProxyOverride);

internal static class InternetSettingsNotifier
{
    private const int InternetOptionSettingsChanged = 39;
    private const int InternetOptionRefresh = 37;

    public static void Notify()
    {
        InternetSetOption(IntPtr.Zero, InternetOptionSettingsChanged, IntPtr.Zero, 0);
        InternetSetOption(IntPtr.Zero, InternetOptionRefresh, IntPtr.Zero, 0);
    }

    [System.Runtime.InteropServices.DllImport("wininet.dll", SetLastError = true)]
    private static extern bool InternetSetOption(IntPtr hInternet, int dwOption, IntPtr lpBuffer, int dwBufferLength);
}

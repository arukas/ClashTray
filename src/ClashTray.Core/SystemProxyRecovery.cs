using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32;

namespace ClashTray.Core;

/// <summary>
/// Best-effort recovery used by the packaged service when the desktop process is
/// no longer available to run its normal per-user cleanup path.
/// </summary>
public static class SystemProxyRecovery
{
    private const string InternetSettingsPath = "Software\\Microsoft\\Windows\\CurrentVersion\\Internet Settings";
    private const string ProfileListPath = "SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\ProfileList";
    private readonly static JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static void RestoreOwnedStatesForLoadedUsers()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using RegistryKey? profiles = Registry.LocalMachine.OpenSubKey(ProfileListPath, writable: false);
        if (profiles is null)
        {
            return;
        }

        foreach (string sid in profiles.GetSubKeyNames())
        {
            try
            {
                RestoreForProfile(sid, profiles.OpenSubKey(sid, writable: false));
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
            catch (JsonException)
            {
            }
        }
    }

    private static void RestoreForProfile(string sid, RegistryKey? profile)
    {
        using (profile)
        {
            string? profilePath = profile?.GetValue("ProfileImagePath") as string;
            if (string.IsNullOrWhiteSpace(profilePath))
            {
                return;
            }

            profilePath = Environment.ExpandEnvironmentVariables(profilePath);
            string localRoot = Path.Combine(profilePath, "AppData", "Local", "ClashTray");
            string backupPath = Path.Combine(localRoot, "system-proxy-backup.json");
            string ownershipPath = Path.Combine(localRoot, "system-proxy-ownership.json");
            if (!File.Exists(backupPath) || !File.Exists(ownershipPath))
            {
                return;
            }

            ProxyRegistryState? backup = JsonSerializer.Deserialize<ProxyRegistryState>(File.ReadAllText(backupPath), JsonOptions);
            ProxyOwnershipState? ownership = JsonSerializer.Deserialize<ProxyOwnershipState>(File.ReadAllText(ownershipPath), JsonOptions);
            if (backup is null || ownership is null)
            {
                return;
            }

            using RegistryKey? internetSettings = Registry.Users.OpenSubKey($"{sid}\\{InternetSettingsPath}", writable: true);
            if (internetSettings is null || !IsOwnedByClashTray(internetSettings, ownership))
            {
                return;
            }

            WriteState(internetSettings, backup);
            File.Delete(backupPath);
            File.Delete(ownershipPath);
            NotifyAllUsers();
        }
    }

    private static bool IsOwnedByClashTray(RegistryKey key, ProxyOwnershipState ownership) =>
        GetDword(key, "ProxyEnable") == 1
        && string.Equals(key.GetValue("ProxyServer") as string, ownership.ProxyServer, StringComparison.OrdinalIgnoreCase)
        && string.Equals(key.GetValue("ProxyOverride") as string ?? string.Empty, ownership.ProxyOverride ?? string.Empty, StringComparison.Ordinal);

    private static void WriteState(RegistryKey key, ProxyRegistryState state)
    {
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

    private static int GetDword(RegistryKey key, string name) =>
        key.GetValue(name) is int value ? value : 0;

    private static void NotifyAllUsers()
    {
        _ = SendMessageTimeout(
            new IntPtr(-1),
            0x001A,
            IntPtr.Zero,
            IntPtr.Zero,
            0x0002,
            2000,
            out _);
    }

    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(
        IntPtr windowHandle,
        uint message,
        IntPtr wParam,
        IntPtr lParam,
        uint flags,
        uint timeout,
        out IntPtr result);
}

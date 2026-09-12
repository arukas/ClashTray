namespace ClashTray.Core;

public sealed class AppPaths
{
    public AppPaths(
        string? localAppData = null,
        string? programData = null)
    {
        LocalRoot = localAppData ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ClashTray");
        ProgramRoot = programData ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "ClashTray");
    }

    public string LocalRoot { get; }

    public string ProgramRoot { get; }

    public string CoreRoot => Path.Combine(ProgramRoot, "core");

    public string ManagedCoreExecutable => Path.Combine(CoreRoot, "mihomo.exe");

    public string ManagedCoreMetadata => Path.Combine(CoreRoot, "mihomo.manifest.json");

    public string ConfigurationsRoot => Path.Combine(LocalRoot, "configurations");

    public string RuntimeRoot => Path.Combine(ProgramRoot, "runtime");

    public string LogsRoot => Path.Combine(LocalRoot, "logs");

    public string SettingsFile => Path.Combine(LocalRoot, "settings.json");

    public string ProxyBackupFile => Path.Combine(LocalRoot, "system-proxy-backup.json");

    public string ProxyOwnershipFile => Path.Combine(LocalRoot, "system-proxy-ownership.json");

    public void EnsureDirectories()
    {
        Directory.CreateDirectory(LocalRoot);
        Directory.CreateDirectory(ProgramRoot);
        Directory.CreateDirectory(ConfigurationsRoot);
        Directory.CreateDirectory(RuntimeRoot);
        Directory.CreateDirectory(LogsRoot);
        WindowsPathSecurity.ProtectRuntimeDirectory(RuntimeRoot);
    }

    public void EnsureProgramDataDirectories(string? managedUserSid = null)
    {
        Directory.CreateDirectory(ProgramRoot);
        Directory.CreateDirectory(CoreRoot);
        Directory.CreateDirectory(RuntimeRoot);
        WindowsPathSecurity.ProtectRuntimeDirectory(RuntimeRoot);
        if (!string.IsNullOrWhiteSpace(managedUserSid))
        {
            WindowsPathSecurity.ProtectManagedCoreDirectory(CoreRoot, managedUserSid);
        }
    }
}

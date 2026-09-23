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

    public string ExternalUiRoot => Path.Combine(ProgramRoot, "ui");

    public string ExternalUiEntryPoint => Path.Combine(ExternalUiRoot, "index.html");

    public string ConfigurationsRoot => Path.Combine(LocalRoot, "configurations");

    public string RuntimeRoot => Path.Combine(ProgramRoot, "runtime");

    public string ServiceLogsRoot => Path.Combine(ProgramRoot, "logs");

    public string LogsRoot => Path.Combine(LocalRoot, "logs");

    public string SettingsFile => Path.Combine(LocalRoot, "settings.json");

    public string ConfigurationSwitchJournalFile =>
        Path.Combine(LocalRoot, "configuration-switch-journal.json");

    public string ConfigurationSwitchBackupsRoot =>
        Path.Combine(LocalRoot, "configuration-switch-backups");

    public string NetworkRulesFile => Path.Combine(LocalRoot, "network-rules.json");

    public string ProxyBackupFile => Path.Combine(LocalRoot, "system-proxy-backup.json");

    public string ProxyOwnershipFile => Path.Combine(LocalRoot, "system-proxy-ownership.json");

    public string ProxyTransactionFile => Path.Combine(LocalRoot, "system-proxy-transaction.json");

    public string LocalCoreShutdownFile => Path.Combine(LocalRoot, "local-core-shutdown.json");

    public string EndpointStoreFile => Path.Combine(LocalRoot, "endpoints.json");

    public string EndpointSecretsFile => Path.Combine(LocalRoot, "endpoint-secrets.json");

    public string EndpointCertificatesFile => Path.Combine(LocalRoot, "endpoint-certificates.json");

    public void EnsureDirectories()
    {
        Directory.CreateDirectory(LocalRoot);
        Directory.CreateDirectory(ProgramRoot);
        Directory.CreateDirectory(ConfigurationsRoot);
        Directory.CreateDirectory(ConfigurationSwitchBackupsRoot);
        Directory.CreateDirectory(RuntimeRoot);
        Directory.CreateDirectory(LogsRoot);
        WindowsPathSecurity.ProtectRuntimeDirectory(RuntimeRoot);
        WindowsPathSecurity.ProtectRuntimeDirectory(ConfigurationSwitchBackupsRoot);
    }

    public void EnsureProgramDataDirectories(string? managedUserSid = null)
    {
        Directory.CreateDirectory(ProgramRoot);
        Directory.CreateDirectory(CoreRoot);
        Directory.CreateDirectory(RuntimeRoot);
        Directory.CreateDirectory(ServiceLogsRoot);
        WindowsPathSecurity.ProtectRuntimeDirectory(RuntimeRoot);
        WindowsPathSecurity.ProtectServiceLogDirectory(ServiceLogsRoot);
        if (!string.IsNullOrWhiteSpace(managedUserSid))
        {
            WindowsPathSecurity.ProtectManagedCoreDirectory(CoreRoot, managedUserSid);
        }
    }
}

namespace ClashTray.Core;

internal interface ICoreUpdatePermissionPolicy
{
    public void PrepareDirectories(AppPaths paths, string? managedUserSid);
    public void ProtectInstalledFiles(AppPaths paths, string? managedUserSid, bool protectDirectory);
}

internal sealed class WindowsCoreUpdatePermissionPolicy : ICoreUpdatePermissionPolicy
{
    public void PrepareDirectories(AppPaths paths, string? managedUserSid) => paths.EnsureProgramDataDirectories(managedUserSid);
    public void ProtectInstalledFiles(AppPaths paths, string? managedUserSid, bool protectDirectory)
    {
        if (string.IsNullOrWhiteSpace(managedUserSid)) { return; }
        if (protectDirectory) { WindowsPathSecurity.ProtectManagedCoreDirectory(paths.CoreRoot, managedUserSid); }
        WindowsPathSecurity.ProtectManagedCoreFile(paths.ManagedCoreExecutable, managedUserSid);
        WindowsPathSecurity.ProtectManagedCoreFile(paths.ManagedCoreMetadata, managedUserSid);
    }
}

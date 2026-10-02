namespace ClashTray.Core.Tests;

// Logic tests exercise downloads/atomic replacement without service-owned log
// ACLs. The real Windows policy is exercised by the mandatory integration gate.
internal sealed class FixtureUpdatePermissions : ICoreUpdatePermissionPolicy
{
    public void PrepareDirectories(AppPaths paths, string? managedUserSid)
    {
        paths.EnsureDirectories();
        Directory.CreateDirectory(paths.CoreRoot);
    }
    public void ProtectInstalledFiles(AppPaths paths, string? managedUserSid, bool protectDirectory)
    {
        WindowsPathSecurity.ProtectRuntimeFile(paths.ManagedCoreExecutable);
        WindowsPathSecurity.ProtectRuntimeFile(paths.ManagedCoreMetadata);
    }
}

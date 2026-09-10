using System.Security.AccessControl;
using System.Security.Principal;

namespace ClashTray.Core;

internal static class WindowsPathSecurity
{
    public static void ProtectRuntimeDirectory(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        SecurityIdentifier currentUser = WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException("Unable to resolve the current Windows user SID.");
        SecurityIdentifier administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        SecurityIdentifier localSystem = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        DirectorySecurity security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        AddFullControlRule(security, currentUser);
        AddFullControlRule(security, administrators);
        AddFullControlRule(security, localSystem);
        new DirectoryInfo(path).SetAccessControl(security);
    }

    public static void ProtectRuntimeFile(string path)
    {
        if (!OperatingSystem.IsWindows() || !File.Exists(path))
        {
            return;
        }

        SecurityIdentifier currentUser = WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException("Unable to resolve the current Windows user SID.");
        SecurityIdentifier administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        SecurityIdentifier localSystem = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        FileSecurity security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        AddFullControlRule(security, currentUser);
        AddFullControlRule(security, administrators);
        AddFullControlRule(security, localSystem);
        new FileInfo(path).SetAccessControl(security);
    }

    private static void AddFullControlRule(FileSystemSecurity security, SecurityIdentifier sid)
    {
        InheritanceFlags inheritance = security is DirectorySecurity
            ? InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit
            : InheritanceFlags.None;
        security.AddAccessRule(new FileSystemAccessRule(
            sid,
            FileSystemRights.FullControl,
            inheritance,
            PropagationFlags.None,
            AccessControlType.Allow));
    }
}

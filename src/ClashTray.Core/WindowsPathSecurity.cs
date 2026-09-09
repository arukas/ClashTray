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

        var currentUser = WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException("Unable to resolve the current Windows user SID.");
        var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var localSystem = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var security = new DirectorySecurity();
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

        var currentUser = WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException("Unable to resolve the current Windows user SID.");
        var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var localSystem = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        AddFullControlRule(security, currentUser);
        AddFullControlRule(security, administrators);
        AddFullControlRule(security, localSystem);
        new FileInfo(path).SetAccessControl(security);
    }

    private static void AddFullControlRule(FileSystemSecurity security, SecurityIdentifier sid)
    {
        var inheritance = security is DirectorySecurity
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

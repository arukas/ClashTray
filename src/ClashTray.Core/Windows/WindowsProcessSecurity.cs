using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace ClashTray.Core;

/// <summary>Lets the bound desktop user verify a service-owned core without controlling its process.</summary>
internal static class WindowsProcessSecurity
{
    private const int DaclSecurityInformation = 0x00000004;
    private const int ErrorInsufficientBuffer = 122;
    private const int QueryLimitedInformation = 0x1000;

    public static void GrantIdentityQueryAccess(SafeProcessHandle processHandle, SecurityIdentifier user)
    {
        ArgumentNullException.ThrowIfNull(processHandle);
        ArgumentNullException.ThrowIfNull(user);
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Process identity query permissions require Windows.");
        }

        // Use the creator's existing handle, never reopen an untrusted or reused PID.
        if (!GetKernelObjectSecurity(processHandle, DaclSecurityInformation, null, 0, out int length)
            && Marshal.GetLastWin32Error() != ErrorInsufficientBuffer)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to read the managed Mihomo process permissions.");
        }

        byte[] bytes = new byte[length];
        if (!GetKernelObjectSecurity(processHandle, DaclSecurityInformation, bytes, bytes.Length, out _))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to read the managed Mihomo process permissions.");
        }

        CommonSecurityDescriptor security = new(false, false, bytes, 0);
        if (security.DiscretionaryAcl is not { } dacl)
        {
            // A null DACL already permits access; do not replace it or change its semantics.
            return;
        }
        dacl.AddAccess(AccessControlType.Allow, user, QueryLimitedInformation,
            InheritanceFlags.None, PropagationFlags.None);
        bytes = new byte[security.BinaryLength];
        security.GetBinaryForm(bytes, 0);
        // Only the DACL is persisted. Existing service/admin ACEs, owner and SACL stay intact.
        if (!SetKernelObjectSecurity(processHandle, DaclSecurityInformation, bytes))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to grant the bound desktop user Mihomo process identity query access.");
        }
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetKernelObjectSecurity(SafeProcessHandle handle, int securityInformation,
        [Out] byte[]? securityDescriptor, int length, out int lengthNeeded);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetKernelObjectSecurity(SafeProcessHandle handle, int securityInformation,
        byte[] securityDescriptor);
}

using System.Diagnostics;
using System.ComponentModel;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using ClashTray.Core;
using Microsoft.Win32.SafeHandles;

namespace ClashTray.IntegrationTests;

[TestClass]
public sealed class QueryLimitedProcessIdentityTests
{
    private const uint QueryLimitedInformation = 0x1000;
    private const int DaclSecurityInformation = 0x00000004;
    private const uint DisableMaximumPrivileges = 0x1;

    [TestMethod]
    [TestCategory("RequiresRestrictedToken")]
    public async Task QueryOnlyProcessDaclStillConfirmsImageCreationTimeAndListenerOwner()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("The Windows process ACL and IP Helper owner-table regression requires Windows.");
            return;
        }

        using Process child = StartListenerChild();
        try
        {
            string portLine = await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10))
                ?? throw new InvalidOperationException("Listener child exited before reporting its port.");
            Assert.IsTrue(int.TryParse(portLine, out int port), $"Listener child returned invalid port '{portLine}'.");

            LocalCoreProcessIdentity expected = new(
                child.Id,
                child.StartTime.ToUniversalTime().Ticks,
                Path.GetFullPath(child.MainModule!.FileName!));
            using WindowsIdentity callerIdentity = WindowsIdentity.GetCurrent(
                TokenAccessLevels.Duplicate | TokenAccessLevels.Query);
            SetQueryOnlyDacl(child.SafeHandle, callerIdentity.User!);
            using SafeAccessTokenHandle queryCaller = CreateCallerWithoutPrivileges(callerIdentity.AccessToken);

            // Hosted runners can have SeDebugPrivilege, which bypasses the child
            // DACL. Keep both controls in the same restricted impersonation scope.
            WindowsIdentity.RunImpersonated(queryCaller, () =>
            {
                using (Process restrictedView = Process.GetProcessById(child.Id))
                {
                    Assert.ThrowsExactly<Win32Exception>(() => _ = restrictedView.MainModule);
                    Assert.ThrowsExactly<Win32Exception>(() => _ = restrictedView.SafeHandle);
                }

                Assert.IsTrue(
                    WindowsListenerOwnerTable.IsCurrentProcessIdentity(expected),
                    "Identity verification should use PROCESS_QUERY_LIMITED_INFORMATION, not Process.SafeHandle all-access.");
                Assert.IsTrue(
                    WindowsListenerOwnerTable.IsOwnedBy(IPAddress.Loopback, port, PortTransport.Tcp, expected),
                    "The listener owner PID must be joined to the verified image and creation time.");
                Assert.IsFalse(WindowsListenerOwnerTable.IsCurrentProcessIdentity(expected with
                {
                    StartTimeUtcTicks = expected.StartTimeUtcTicks + 1
                }));
            });
        }
        finally
        {
            if (!child.HasExited)
            {
                child.Kill(entireProcessTree: true);
                await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            }
        }
    }

    private static SafeAccessTokenHandle CreateCallerWithoutPrivileges(SafeAccessTokenHandle callerToken)
    {
        if (!CreateRestrictedToken(
            callerToken,
            DisableMaximumPrivileges,
            disableSidCount: 0,
            sidsToDisable: IntPtr.Zero,
            deletePrivilegeCount: 0,
            privilegesToDelete: IntPtr.Zero,
            restrictedSidCount: 0,
            sidsToRestrict: IntPtr.Zero,
            out SafeAccessTokenHandle restrictedToken))
        {
            int error = Marshal.GetLastWin32Error();
            throw new Win32Exception(error, $"EnvironmentCapabilityUnavailable: CreateRestrictedToken failed with Win32 error {error}. Run the required token gate with a Windows token that supports restricted-token creation.");
        }

        return restrictedToken;
    }

    private static Process StartListenerChild()
    {
        string windowsPowerShell = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "System32",
            "WindowsPowerShell",
            "v1.0",
            "powershell.exe");
        ProcessStartInfo startInfo = new(windowsPowerShell)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        startInfo.ArgumentList.Add("-NoLogo");
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add(
            "$listener=[System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback,0);"
            + "$listener.Start();[Console]::WriteLine($listener.LocalEndpoint.Port);[Console]::Out.Flush();"
            + "Start-Sleep -Seconds 120");
        return Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start the isolated process identity fixture.");
    }

    private static void SetQueryOnlyDacl(SafeProcessHandle processHandle, SecurityIdentifier userSid)
    {
        RawAcl dacl = new(GenericAcl.AclRevision, 1);
        dacl.InsertAce(
            0,
            new CommonAce(
                AceFlags.None,
                AceQualifier.AccessAllowed,
                checked((int)QueryLimitedInformation),
                userSid,
                isCallback: false,
                opaque: null));
        RawSecurityDescriptor descriptor = new(
            ControlFlags.DiscretionaryAclPresent,
            userSid,
            userSid,
            systemAcl: null,
            dacl);
        byte[] bytes = new byte[descriptor.BinaryLength];
        descriptor.GetBinaryForm(bytes, 0);
        IntPtr memory = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, memory, bytes.Length);
            if (!SetKernelObjectSecurity(processHandle, DaclSecurityInformation, memory))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not restrict the isolated child process DACL.");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(memory);
        }
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetKernelObjectSecurity(
        SafeProcessHandle handle,
        int securityInformation,
        IntPtr securityDescriptor);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateRestrictedToken(
        SafeAccessTokenHandle existingToken,
        uint flags,
        uint disableSidCount,
        IntPtr sidsToDisable,
        uint deletePrivilegeCount,
        IntPtr privilegesToDelete,
        uint restrictedSidCount,
        IntPtr sidsToRestrict,
        out SafeAccessTokenHandle newToken);
}

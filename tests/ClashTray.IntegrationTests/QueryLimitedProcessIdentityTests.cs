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
    public async Task ServiceOnlyProcessBecomesQueryableWithoutGrantingDesktopProcessControl()
    {
        using Process child = StartListenerChild();
        Task<string> errors = child.StandardError.ReadToEndAsync();
        try
        {
            string? portLine = await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30));
            Assert.IsTrue(int.TryParse(portLine, out int port), $"Listener child returned invalid port '{portLine}'.");
            LocalCoreProcessIdentity expected = WindowsListenerOwnerTable.CaptureProcessIdentity(child.Id);
            using WindowsIdentity caller = WindowsIdentity.GetCurrent(TokenAccessLevels.Duplicate | TokenAccessLevels.Query);
            using SafeAccessTokenHandle desktop = CreateCallerWithoutPrivileges(caller.AccessToken);
            SetServiceOnlyDacl(child.SafeHandle, caller.User!);
            WindowsIdentity.RunImpersonated(desktop, () =>
            {
                Win32Exception denied = Assert.ThrowsExactly<Win32Exception>(() => WindowsListenerOwnerTable.CaptureProcessIdentity(child.Id));
                Assert.AreEqual(5, denied.NativeErrorCode);
                Assert.IsFalse(WindowsListenerOwnerTable.IsOwnedBy(IPAddress.Loopback, port, PortTransport.Tcp, expected));
            });

            WindowsProcessSecurity.GrantIdentityQueryAccess(child.SafeHandle, caller.User!);
            WindowsProcessSecurity.GrantIdentityQueryAccess(child.SafeHandle, caller.User!);
            RawSecurityDescriptor descriptor = ReadProcessSecurity(child.SafeHandle);
            RawAcl dacl = descriptor.DiscretionaryAcl!;
            CommonAce[] grants = dacl.OfType<CommonAce>()
                .Where(ace => ace.SecurityIdentifier == caller.User).ToArray();
            Assert.HasCount(1, grants, "Repeated grants must not accumulate duplicate ACEs.");
            Assert.AreEqual((int)QueryLimitedInformation, grants[0].AccessMask);
            Assert.IsTrue(dacl.OfType<CommonAce>().Any(ace =>
                ace.SecurityIdentifier.IsWellKnown(WellKnownSidType.LocalSystemSid) && ace.AccessMask == 0x1fffff),
                "The service's existing process control permissions must be preserved.");
            WindowsIdentity.RunImpersonated(desktop, () =>
            {
                Assert.AreEqual(expected, WindowsListenerOwnerTable.CaptureProcessIdentity(child.Id));
                Assert.IsTrue(WindowsListenerOwnerTable.IsOwnedBy(IPAddress.Loopback, port, PortTransport.Tcp, expected));
                Assert.IsFalse(WindowsListenerOwnerTable.IsCurrentProcessIdentity(expected with { StartTimeUtcTicks = expected.StartTimeUtcTicks + 1 }));
                foreach (uint access in new uint[] { 0x1, 0x2, 0x20, 0x40, 0x40000 })
                {
                    using SafeProcessHandle denied = OpenProcess(access, false, child.Id);
                    Assert.IsTrue(denied.IsInvalid, $"Desktop must not receive process access 0x{access:x}.");
                    Assert.AreEqual(5, Marshal.GetLastWin32Error());
                }
            });
        }
        finally
        {
            if (!child.HasExited)
            {
                child.Kill(entireProcessTree: true);
                await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            }
            string error = await errors.WaitAsync(TimeSpan.FromSeconds(5));
            if (!string.IsNullOrWhiteSpace(error)) { await Console.Error.WriteLineAsync(error); }
        }
    }

    internal static void SetServiceOnlyDacl(SafeProcessHandle handle, SecurityIdentifier owner)
    {
        RawAcl dacl = new(GenericAcl.AclRevision, 2);
        dacl.InsertAce(0, new CommonAce(AceFlags.None, AceQualifier.AccessAllowed, 0x1fffff,
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), false, null));
        // Suppress the fixture owner's implicit WRITE_DAC, matching a desktop user
        // that does not own the production LocalSystem process.
        dacl.InsertAce(1, new CommonAce(AceFlags.None, AceQualifier.AccessAllowed, 0x20000,
            new SecurityIdentifier("S-1-3-4"), false, null));
        SetProcessDacl(handle, new RawSecurityDescriptor(ControlFlags.DiscretionaryAclPresent,
            owner, owner, null, dacl));
    }

    internal static RawSecurityDescriptor ReadProcessSecurity(SafeProcessHandle handle)
    {
        _ = GetKernelObjectSecurity(handle, DaclSecurityInformation, null, 0, out int length);
        byte[] bytes = new byte[length];
        if (!GetKernelObjectSecurity(handle, DaclSecurityInformation, bytes, bytes.Length, out _))
        { throw new Win32Exception(Marshal.GetLastWin32Error()); }
        return new RawSecurityDescriptor(bytes, 0);
    }

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
        Task<string> childErrorOutput = child.StandardError.ReadToEndAsync();
        try
        {
            using CancellationTokenSource startupDeadline = new(TimeSpan.FromSeconds(30));
            string? portLine;
            try
            {
                portLine = await child.StandardOutput.ReadLineAsync(startupDeadline.Token).AsTask().WaitAsync(startupDeadline.Token);
            }
            catch (OperationCanceledException exception) when (startupDeadline.IsCancellationRequested)
            {
                throw new TimeoutException(
                    $"Listener child {child.Id} did not report its port within 30 seconds; exited={child.HasExited}.",
                    exception);
            }

            if (portLine is null)
            {
                string childState = child.HasExited ? $"exit code={child.ExitCode}" : "stdout closed while still running";
                throw new InvalidOperationException($"Listener child {child.Id} closed stdout before reporting its port; {childState}.");
            }
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

            string errorOutput = await childErrorOutput.WaitAsync(TimeSpan.FromSeconds(5));
            if (!string.IsNullOrWhiteSpace(errorOutput))
            {
                await Console.Error.WriteLineAsync($"Listener child stderr: {errorOutput}");
            }
        }
    }

    internal static SafeAccessTokenHandle CreateCallerWithoutPrivileges(SafeAccessTokenHandle callerToken)
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
        SetProcessDacl(processHandle, descriptor);
    }

    private static void SetProcessDacl(SafeProcessHandle processHandle, RawSecurityDescriptor descriptor)
    {
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
    private static extern bool GetKernelObjectSecurity(SafeProcessHandle handle, int securityInformation,
        [Out] byte[]? descriptor, int length, out int lengthNeeded);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, int processId);

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

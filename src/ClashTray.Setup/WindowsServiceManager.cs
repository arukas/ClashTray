using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace ClashTray.Setup;

internal static class WindowsServiceManager
{
    private const string ServiceName = "ClashTrayService";
    private const string ServiceExecutableName = "ClashTray.Service.exe";

    private const uint ScManagerConnect = 0x0001;
    private const uint ScManagerCreateService = 0x0002;
    private const uint ServiceQueryConfig = 0x0001;
    private const uint ServiceChangeConfig = 0x0002;
    private const uint ServiceQueryStatus = 0x0004;
    private const uint ServiceStart = 0x0010;
    private const uint ServiceStop = 0x0020;
    private const uint Delete = 0x00010000;
    private const uint ServiceWin32OwnProcess = 0x00000010;
    private const uint ServiceAutoStart = 0x00000002;
    private const uint ServiceErrorNormal = 0x00000001;
    private const uint ServiceControlStop = 0x00000001;
    private const uint ServiceStopped = 0x00000001;
    private const uint ServiceStartPending = 0x00000002;
    private const uint ServiceStopPending = 0x00000003;
    private const uint ServiceRunning = 0x00000004;
    private const uint ServiceConfigDescription = 1;

    private const int ErrorInsufficientBuffer = 122;
    private const int ErrorServiceDoesNotExist = 1060;
    private const int ErrorServiceAlreadyRunning = 1056;
    private const int ErrorServiceNotActive = 1062;
    private const int ErrorServiceMarkedForDelete = 1072;

    public static void InstallOrUpdate(string serviceExecutablePath, string userSid)
    {
        var fullServicePath = Path.GetFullPath(serviceExecutablePath);
        SetupLog.Write($"准备安装服务，路径：{fullServicePath}");
        if (!File.Exists(fullServicePath)
            || !string.Equals(Path.GetFileName(fullServicePath), ServiceExecutableName, StringComparison.OrdinalIgnoreCase))
        {
            throw new FileNotFoundException("ClashTray 服务文件不存在或名称不正确。", fullServicePath);
        }

        try
        {
            _ = new SecurityIdentifier(userSid);
        }
        catch (Exception exception) when (exception is ArgumentException or IdentityNotMappedException)
        {
            throw new InvalidOperationException("当前 Windows 用户标识无效。", exception);
        }

        var managerHandle = OpenManager(ScManagerConnect | ScManagerCreateService);
        try
        {
            var existingPath = GetExistingBinaryPath(managerHandle);
            SetupLog.Write($"现有服务路径：{existingPath ?? "不存在"}");
            if (existingPath is not null)
            {
                if (!IsOwnedServicePath(existingPath))
                {
                    throw new InvalidOperationException($"系统中已有同名服务，但路径不是 ClashTray 服务：{existingPath}");
                }

                RemoveExisting(managerHandle);
                SetupLog.Write("旧服务删除完成。");
            }

            var serviceHandle = CreateService(
                managerHandle,
                ServiceName,
                "ClashTray Service",
                ServiceQueryStatus | ServiceChangeConfig | ServiceStart | ServiceStop | Delete,
                ServiceWin32OwnProcess,
                ServiceAutoStart,
                ServiceErrorNormal,
                $"\"{fullServicePath}\" --user-sid={userSid}",
                null,
                IntPtr.Zero,
                null,
                null,
                null);
            if (serviceHandle == IntPtr.Zero)
            {
                ThrowLastWin32("创建 ClashTray 服务失败");
            }

            try
            {
                SetDescription(serviceHandle, "ClashTray Mihomo 生命周期和 TUN 服务");
                SetupLog.Write("新服务创建完成，准备启动。");
                if (!StartService(serviceHandle, 0, IntPtr.Zero))
                {
                    var error = Marshal.GetLastWin32Error();
                    if (error != ErrorServiceAlreadyRunning)
                    {
                        ThrowWin32("启动 ClashTray 服务失败", error);
                    }
                }

                WaitForState(serviceHandle, ServiceRunning, TimeSpan.FromSeconds(10));
                SetupLog.Write("新服务已进入运行状态。");
            }
            finally
            {
                CloseServiceHandle(serviceHandle);
            }
        }
        finally
        {
            CloseServiceHandle(managerHandle);
        }
    }

    public static void RemoveOwnedService()
    {
        SetupLog.Write("准备删除已有 ClashTray 服务。");
        var managerHandle = OpenManager(ScManagerConnect);
        try
        {
            var serviceHandle = OpenService(
                managerHandle,
                ServiceName,
                ServiceQueryConfig | ServiceQueryStatus | ServiceStop | Delete);
            if (serviceHandle == IntPtr.Zero)
            {
                var error = Marshal.GetLastWin32Error();
                if (error == ErrorServiceDoesNotExist)
                {
                    SetupLog.Write("已有 ClashTray 服务不存在，无需删除。");
                    return;
                }

                ThrowWin32("打开 ClashTray 服务失败", error);
            }

            try
            {
                var binaryPath = GetBinaryPath(serviceHandle);
                if (binaryPath is not null && !IsOwnedServicePath(binaryPath))
                {
                    throw new InvalidOperationException($"系统中已有同名服务，但路径不是 ClashTray 服务：{binaryPath}");
                }

                StopService(serviceHandle);
                if (!DeleteService(serviceHandle))
                {
                    var error = Marshal.GetLastWin32Error();
                    if (error != ErrorServiceMarkedForDelete)
                    {
                        ThrowWin32("删除 ClashTray 服务失败", error);
                    }
                }
            }
            finally
            {
                CloseServiceHandle(serviceHandle);
            }

            WaitForRemoval(managerHandle);
            SetupLog.Write("已有 ClashTray 服务删除确认完成。");
        }
        finally
        {
            CloseServiceHandle(managerHandle);
        }
    }

    private static void RemoveExisting(IntPtr managerHandle)
    {
        SetupLog.Write("安装阶段发现已有服务，准备删除并重建。");
        var serviceHandle = OpenService(managerHandle, ServiceName, ServiceQueryStatus | ServiceStop | Delete);
        if (serviceHandle == IntPtr.Zero)
        {
            var error = Marshal.GetLastWin32Error();
            if (error == ErrorServiceDoesNotExist)
            {
                return;
            }

            ThrowWin32("打开旧 ClashTray 服务失败", error);
        }

        try
        {
            StopService(serviceHandle);
            if (!DeleteService(serviceHandle))
            {
                var error = Marshal.GetLastWin32Error();
                if (error != ErrorServiceMarkedForDelete)
                {
                    ThrowWin32("删除旧 ClashTray 服务失败", error);
                }
            }
        }
        finally
        {
            CloseServiceHandle(serviceHandle);
        }

        WaitForRemoval(managerHandle);
    }

    private static IntPtr OpenManager(uint access)
    {
        var handle = OpenSCManager(null, null, access);
        if (handle == IntPtr.Zero)
        {
            ThrowLastWin32("打开 Windows 服务控制管理器失败");
        }

        return handle;
    }

    private static string? GetExistingBinaryPath(IntPtr managerHandle)
    {
        var serviceHandle = OpenService(managerHandle, ServiceName, ServiceQueryConfig);
        if (serviceHandle == IntPtr.Zero)
        {
            var error = Marshal.GetLastWin32Error();
            if (error == ErrorServiceDoesNotExist)
            {
                return null;
            }

            if (error == ErrorServiceMarkedForDelete)
            {
                WaitForRemoval(managerHandle);
                return null;
            }

            ThrowWin32("读取 ClashTray 服务失败", error);
        }

        try
        {
            return GetBinaryPath(serviceHandle);
        }
        finally
        {
            CloseServiceHandle(serviceHandle);
        }
    }

    private static string? GetBinaryPath(IntPtr serviceHandle)
    {
        QueryServiceConfig(serviceHandle, IntPtr.Zero, 0, out var bytesNeeded);
        var firstError = Marshal.GetLastWin32Error();
        if (bytesNeeded <= 0 && firstError != ErrorInsufficientBuffer)
        {
            ThrowWin32("读取 ClashTray 服务配置失败", firstError);
        }

        if (bytesNeeded <= 0)
        {
            throw new InvalidOperationException("Windows 服务配置大小无效。");
        }

        var buffer = Marshal.AllocHGlobal(bytesNeeded);
        try
        {
            if (!QueryServiceConfig(serviceHandle, buffer, bytesNeeded, out _))
            {
                ThrowLastWin32("读取 ClashTray 服务路径失败");
            }

            var config = Marshal.PtrToStructure<QueryServiceConfigData>(buffer);
            return config.lpBinaryPathName == IntPtr.Zero
                ? null
                : Marshal.PtrToStringUni(config.lpBinaryPathName);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static bool IsOwnedServicePath(string path)
    {
        var candidate = path.Trim();
        if (candidate.StartsWith('"'))
        {
            var closingQuote = candidate.IndexOf('"', 1);
            candidate = closingQuote > 1 ? candidate[1..closingQuote] : candidate.Trim('"');
        }
        else
        {
            var executableMarker = candidate.IndexOf(ServiceExecutableName, StringComparison.OrdinalIgnoreCase);
            if (executableMarker >= 0)
            {
                candidate = candidate[..(executableMarker + ServiceExecutableName.Length)];
            }
        }

        return string.Equals(Path.GetFileName(candidate), ServiceExecutableName, StringComparison.OrdinalIgnoreCase)
            && candidate.Contains("ClashTray", StringComparison.OrdinalIgnoreCase);
    }

    private static void StopService(IntPtr serviceHandle)
    {
        if (!QueryServiceStatus(serviceHandle, out var status))
        {
            ThrowLastWin32("读取 ClashTray 服务状态失败");
        }

        if (status.dwCurrentState == ServiceStopped)
        {
            return;
        }

        if (status.dwCurrentState != ServiceStopPending
            && !ControlService(serviceHandle, ServiceControlStop, out status))
        {
            var error = Marshal.GetLastWin32Error();
            if (error != ErrorServiceNotActive)
            {
                ThrowWin32("停止 ClashTray 服务失败", error);
            }
        }

        WaitForState(serviceHandle, ServiceStopped, TimeSpan.FromSeconds(15));
    }

    private static void WaitForState(IntPtr serviceHandle, uint expectedState, TimeSpan timeout)
    {
        var deadline = Stopwatch.GetTimestamp() + (long)(timeout.TotalSeconds * Stopwatch.Frequency);
        while (Stopwatch.GetTimestamp() < deadline)
        {
            if (!QueryServiceStatus(serviceHandle, out var status))
            {
                ThrowLastWin32("读取 ClashTray 服务状态失败");
            }

            if (status.dwCurrentState == expectedState)
            {
                return;
            }

            if (expectedState == ServiceRunning
                && status.dwCurrentState is not (ServiceStartPending or ServiceRunning))
            {
                throw new InvalidOperationException($"ClashTray 服务未能启动，当前状态码：{status.dwCurrentState}");
            }

            if (expectedState == ServiceStopped
                && status.dwCurrentState is not (ServiceStopPending or ServiceStopped))
            {
                throw new InvalidOperationException($"ClashTray 服务未能停止，当前状态码：{status.dwCurrentState}");
            }

            Thread.Sleep(150);
        }

        throw new TimeoutException($"等待 ClashTray 服务状态 {expectedState} 超时。");
    }

    private static void WaitForRemoval(IntPtr managerHandle)
    {
        var deadline = Stopwatch.GetTimestamp() + (long)(10 * Stopwatch.Frequency);
        while (Stopwatch.GetTimestamp() < deadline)
        {
            var handle = OpenService(managerHandle, ServiceName, ServiceQueryStatus);
            if (handle == IntPtr.Zero)
            {
                var error = Marshal.GetLastWin32Error();
                if (error == ErrorServiceDoesNotExist)
                {
                    return;
                }

                if (error == ErrorServiceMarkedForDelete)
                {
                    Thread.Sleep(150);
                    continue;
                }

                ThrowWin32("等待 ClashTray 服务删除失败", error);
            }
            else
            {
                CloseServiceHandle(handle);
            }

            Thread.Sleep(150);
        }

        throw new TimeoutException("等待 ClashTray 服务删除超时。请重启 Windows 后再运行最新安装包。");
    }

    private static void SetDescription(IntPtr serviceHandle, string description)
    {
        var descriptionPointer = Marshal.StringToHGlobalUni(description);
        try
        {
            var data = new ServiceDescription { lpDescription = descriptionPointer };
            if (!ChangeServiceConfig2(serviceHandle, ServiceConfigDescription, ref data))
            {
                var error = Marshal.GetLastWin32Error();
                if (error is not (5 or 87))
                {
                    ThrowWin32("设置 ClashTray 服务描述失败", error);
                }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(descriptionPointer);
        }
    }

    private static void ThrowLastWin32(string message) => ThrowWin32(message, Marshal.GetLastWin32Error());

    private static void ThrowWin32(string message, int error) => throw new Win32Exception(error, message);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenSCManager(string? machineName, string? databaseName, uint desiredAccess);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenService(IntPtr managerHandle, string serviceName, uint desiredAccess);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateService(
        IntPtr managerHandle,
        string serviceName,
        string displayName,
        uint desiredAccess,
        uint serviceType,
        uint startType,
        uint errorControl,
        string binaryPathName,
        string? loadOrderGroup,
        IntPtr tagId,
        string? dependencies,
        string? serviceStartName,
        string? password);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool CloseServiceHandle(IntPtr serviceHandle);

    [DllImport("advapi32.dll", EntryPoint = "QueryServiceConfigW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryServiceConfig(IntPtr serviceHandle, IntPtr buffer, int bufferSize, out int bytesNeeded);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool QueryServiceStatus(IntPtr serviceHandle, out ServiceStatus status);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool ControlService(IntPtr serviceHandle, uint controlCode, out ServiceStatus status);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool StartService(IntPtr serviceHandle, int argumentCount, IntPtr arguments);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool DeleteService(IntPtr serviceHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool ChangeServiceConfig2(IntPtr serviceHandle, uint infoLevel, ref ServiceDescription description);

    [StructLayout(LayoutKind.Sequential)]
    private struct QueryServiceConfigData
    {
        public uint dwServiceType;
        public uint dwStartType;
        public uint dwErrorControl;
        public IntPtr lpBinaryPathName;
        public IntPtr lpLoadOrderGroup;
        public uint dwTagId;
        public IntPtr lpDependencies;
        public IntPtr lpServiceStartName;
        public IntPtr lpDisplayName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatus
    {
        public uint dwServiceType;
        public uint dwCurrentState;
        public uint dwControlsAccepted;
        public uint dwWin32ExitCode;
        public uint dwServiceSpecificExitCode;
        public uint dwCheckPoint;
        public uint dwWaitHint;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceDescription
    {
        public IntPtr lpDescription;
    }
}

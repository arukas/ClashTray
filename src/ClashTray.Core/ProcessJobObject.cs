using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ClashTray.Core;

/// <summary>
/// Owns a Win32 job object configured with JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE so the
/// managed Mihomo process cannot outlive its host when the host is force-terminated
/// before the shutdown journal or graceful stop paths can run.
/// </summary>
internal sealed class ProcessJobObject : IDisposable
{
    private const uint JobObjectLimitKillOnJobClose = 0x2000;

    private IntPtr _handle;

    private ProcessJobObject(IntPtr handle) => _handle = handle;

    internal IntPtr Handle => _handle;

    public static bool TryCreate(Process process, out ProcessJobObject? job)
    {
        ArgumentNullException.ThrowIfNull(process);
        job = null;
        IntPtr handle = IntPtr.Zero;
        try
        {
            handle = CreateJobObject(IntPtr.Zero, null);
            if (handle == IntPtr.Zero)
            {
                return false;
            }

            JobObjectExtendedLimitInformation information = new()
            {
                BasicLimitInformation = new JobObjectBasicLimitInformation
                {
                    LimitFlags = JobObjectLimitKillOnJobClose
                }
            };
            int length = Marshal.SizeOf<JobObjectExtendedLimitInformation>();
            IntPtr informationPointer = Marshal.AllocHGlobal(length);
            try
            {
                Marshal.StructureToPtr(information, informationPointer, false);
                if (!SetInformationJobObject(
                        handle,
                        JobObjectInformationClass.ExtendedLimitInformation,
                        informationPointer,
                        (uint)length))
                {
                    return false;
                }
            }
            finally
            {
                Marshal.FreeHGlobal(informationPointer);
            }

            if (!AssignProcessToJobObject(handle, process.Handle))
            {
                return false;
            }

            job = new ProcessJobObject(handle);
            handle = IntPtr.Zero;
            return true;
        }
        finally
        {
            if (handle != IntPtr.Zero)
            {
                CloseHandle(handle);
            }
        }
    }

    public void Dispose()
    {
        IntPtr handle = Interlocked.Exchange(ref _handle, IntPtr.Zero);
        if (handle != IntPtr.Zero)
        {
            CloseHandle(handle);
        }
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateJobObject(IntPtr jobAttributes, string? name);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(
        IntPtr job,
        JobObjectInformationClass informationClass,
        IntPtr jobInformation,
        uint jobInformationLength);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    private enum JobObjectInformationClass
    {
        ExtendedLimitInformation = 9
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformation
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }
}

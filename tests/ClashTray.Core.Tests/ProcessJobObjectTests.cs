using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ClashTray.Core.Tests;

[TestClass]
public sealed class ProcessJobObjectTests
{
    [TestMethod]
    public void TryCreateAssignsProcessToJob()
    {
        using Process process = StartLongRunningProcess();
        try
        {
            Assert.IsTrue(ProcessJobObject.TryCreate(process, out ProcessJobObject? job));
            Assert.IsNotNull(job);
            using (job)
            {
                Assert.IsTrue(
                    IsProcessInJob(process.Handle, job.Handle, out bool isInJob) && isInJob,
                    "The process should report membership in the created job object.");
            }
        }
        finally
        {
            KillSilently(process);
        }
    }

    [TestMethod]
    public void ClosingJobHandleTerminatesAssignedProcess()
    {
        using Process process = StartLongRunningProcess();
        Assert.IsTrue(ProcessJobObject.TryCreate(process, out ProcessJobObject? job));
        Assert.IsNotNull(job);

        job.Dispose();

        Assert.IsTrue(
            process.WaitForExit(10_000),
            "KILL_ON_JOB_CLOSE should terminate the process when the last job handle closes.");
    }

    private static Process StartLongRunningProcess()
    {
        ProcessStartInfo startInfo = new("ping.exe")
        {
            CreateNoWindow = true,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add("-n");
        startInfo.ArgumentList.Add("30");
        startInfo.ArgumentList.Add("127.0.0.1");
        return Process.Start(startInfo) ?? throw new InvalidOperationException("Unable to start ping.");
    }

    private static void KillSilently(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsProcessInJob(IntPtr process, IntPtr job, [MarshalAs(UnmanagedType.Bool)] out bool result);
}

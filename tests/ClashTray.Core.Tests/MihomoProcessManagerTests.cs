using ClashTray.Contracts;
using System.Diagnostics;

namespace ClashTray.Core.Tests;

[TestClass]
public sealed class MihomoProcessManagerTests
{
    [TestMethod]
    public async Task RunningStateIsPublishedOnlyAfterCurrentProcessGenerationIsReady()
    {
        string root = Path.Combine(Path.GetTempPath(), "ClashTrayTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string configurationPath = Path.Combine(root, "config.yaml");
        await File.WriteAllTextAsync(configurationPath, string.Empty);
        await using MihomoProcessManager manager = new(
            validationTimeout: TimeSpan.FromSeconds(1),
            stopTimeout: TimeSpan.FromSeconds(1),
            processStartInfoFactory: CreateLongRunningProcess);
        List<CoreState> observed = [];
        manager.StateChanged += (_, state) => observed.Add(state);

        try
        {
            await manager.StartAsync("test-core", configurationPath, root);
            long generation = manager.Generation;

            Assert.AreEqual(CoreState.Starting, manager.State);
            Assert.IsFalse(observed.Contains(CoreState.Running));
            Assert.IsFalse(manager.TryMarkReady(generation - 1));
            Assert.IsTrue(manager.TryMarkReady(generation));
            Assert.AreEqual(CoreState.Running, manager.State);
            Assert.IsTrue(observed.Contains(CoreState.Running));

            await manager.StopAsync();
        }
        finally
        {
            if (manager.State is not CoreState.Stopped)
            {
                await manager.StopAsync();
            }

            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task ValidationTimeoutLeavesManagerInFailedState()
    {
        string ping = Path.Combine(Environment.SystemDirectory, "ping.exe");
        Assert.IsTrue(File.Exists(ping));
        await using MihomoProcessManager manager = new MihomoProcessManager(
            validationTimeout: TimeSpan.FromMilliseconds(100),
            stopTimeout: TimeSpan.FromSeconds(1));

        TimeoutException exception = await Assert.ThrowsExactlyAsync<TimeoutException>(() =>
            manager.ValidateAsync(ping, "127.0.0.1"));

        StringAssert.Contains(exception.Message, "配置验证超过", StringComparison.Ordinal);
        Assert.AreEqual(CoreState.Failed, manager.State);

        await manager.StopAsync();
        Assert.AreEqual(CoreState.Stopped, manager.State);
    }

    [TestMethod]
    public async Task UnexpectedExitIsReportedAndStopRemainsRecoverable()
    {
        string root = Path.Combine(Path.GetTempPath(), "ClashTrayTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string configurationPath = Path.Combine(root, "config.yaml");
        await File.WriteAllTextAsync(configurationPath, string.Empty);
        await using MihomoProcessManager manager = new MihomoProcessManager();
        TaskCompletionSource<bool> failed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        manager.StateChanged += (_, state) =>
        {
            if (state == CoreState.Failed)
            {
                failed.TrySetResult(true);
            }
        };

        try
        {
            string commandProcessor = Path.Combine(Environment.SystemDirectory, "cmd.exe");
            await manager.StartAsync(commandProcessor, configurationPath, root);

            await failed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.AreEqual(CoreState.Failed, manager.State);

            await manager.StopAsync();
            Assert.AreEqual(CoreState.Stopped, manager.State);
        }
        finally
        {
            if (manager.State is not CoreState.Stopped)
            {
                await manager.StopAsync();
            }

            Directory.Delete(root, recursive: true);
        }
    }

    private static Process CreateLongRunningProcess(ProcessStartInfo startInfo)
    {
        startInfo.FileName = Path.Combine(
            Environment.SystemDirectory,
            "WindowsPowerShell",
            "v1.0",
            "powershell.exe");
        startInfo.Arguments = "-NoLogo -NoProfile -NonInteractive -Command \"while ($true) { Start-Sleep -Seconds 1 }\"";
        return new Process { StartInfo = startInfo, EnableRaisingEvents = true };
    }
}

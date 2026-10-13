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
    public async Task ExitedValidatorWithInheritedOutputDoesNotHoldTheOperationLockForever()
    {
        TaskCompletionSource exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
        bool spawnChild = true;
        await using MihomoProcessManager manager = new(
            TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2), null,
            startInfo =>
            {
                startInfo.FileName = Path.Combine(Environment.SystemDirectory, "cmd.exe");
                startInfo.Arguments = spawnChild
                    ? "/d /c start \"\" /b ping.exe -t 127.0.0.1 & exit /b 0"
                    : "/d /c exit /b 0";
                Process process = new() { StartInfo = startInfo, EnableRaisingEvents = true };
                process.Exited += (_, _) => exited.TrySetResult();
                return process;
            });
        Task<bool> validation = manager.ValidateAsync("test-validator", "test.yaml");
        await exited.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsExactlyAsync<TimeoutException>(() => validation.WaitAsync(TimeSpan.FromSeconds(8)));
        Assert.AreEqual(CoreState.Failed, manager.State);
        spawnChild = false;
        Assert.IsTrue(await manager.ValidateAsync("test-validator", "test.yaml").WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.AreEqual(CoreState.Stopped, manager.State);
    }

    [TestMethod]
    public async Task ValidationCallerCancellationRemainsCancellationAndAllowsAnotherValidation()
    {
        using CancellationTokenSource caller = new();
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await using MihomoProcessManager manager = new(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(2), null,
            startInfo =>
            {
                startInfo.FileName = Path.Combine(Environment.SystemDirectory, "ping.exe");
                startInfo.Arguments = "-t 127.0.0.1";
                started.TrySetResult();
                return new Process { StartInfo = startInfo };
            });
        Task<bool> validation = manager.ValidateAsync("test-validator", "test.yaml", caller.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await caller.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(() => validation.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.AreEqual(CoreState.Stopped, manager.State);
        await manager.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
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

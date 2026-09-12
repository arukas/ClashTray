using ClashTray.Contracts;

namespace ClashTray.Core.Tests;

[TestClass]
public sealed class MihomoProcessManagerTests
{
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
}

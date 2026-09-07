using System.Diagnostics;
using ClashTray.Contracts;
using ClashTray.Core;

namespace ClashTray.Core.Tests;

[TestClass]
public sealed class MihomoProcessManagerTests
{
    [TestMethod]
    public async Task UnexpectedExitIsReportedAndStopRemainsRecoverable()
    {
        var root = Path.Combine(Path.GetTempPath(), "ClashTrayTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var configurationPath = Path.Combine(root, "config.yaml");
        await File.WriteAllTextAsync(configurationPath, string.Empty);
        await using var manager = new MihomoProcessManager();
        var failed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        manager.StateChanged += (_, state) =>
        {
            if (state == CoreState.Failed)
            {
                failed.TrySetResult(true);
            }
        };

        try
        {
            var commandProcessor = Path.Combine(Environment.SystemDirectory, "cmd.exe");
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

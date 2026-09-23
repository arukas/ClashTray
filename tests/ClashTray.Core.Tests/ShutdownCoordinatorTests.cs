using ClashTray.App;

namespace ClashTray.Core.Tests;

[TestClass]
public sealed class ShutdownCoordinatorTests
{
    [TestMethod]
    public async Task RepeatedQuitCallsSharePendingCompletionAndExitOnlyAfterCleanup()
    {
        TaskCompletionSource cleanup = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int beginCalls = 0;
        int disposeCalls = 0;
        int exitCalls = 0;
        ShutdownCoordinator coordinator = new(
            () => beginCalls++,
            () =>
            {
                disposeCalls++;
                return cleanup.Task;
            },
            () => exitCalls++);

        Task first = coordinator.RequestQuitAsync();
        Task second = coordinator.RequestQuitAsync();

        try
        {
            Assert.AreSame(first, second);
            Assert.AreEqual(1, beginCalls);
            Assert.AreEqual(1, disposeCalls);
            Assert.AreEqual(0, exitCalls);
            Assert.IsFalse(first.IsCompleted);
        }
        finally
        {
            cleanup.TrySetResult();
            await first;
        }

        Assert.AreEqual(1, exitCalls);
        Assert.IsTrue(first.IsCompletedSuccessfully);
    }
}
namespace ClashTray.Core.Tests;

[TestClass]
public sealed class SnapshotPublishThrottleTests
{
    [TestMethod]
    public async Task BurstRequestsAreCoalescedToOnePublishPerInterval()
    {
        int publishCount = 0;
        TaskCompletionSource firstPublish = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await using SnapshotPublishThrottle throttle = new SnapshotPublishThrottle(
            () =>
            {
                if (Interlocked.Increment(ref publishCount) == 1)
                {
                    firstPublish.TrySetResult();
                }
            },
            CancellationToken.None,
            TimeSpan.FromMilliseconds(150));

        throttle.Queue();
        await firstPublish.Task.WaitAsync(TimeSpan.FromSeconds(2));

        for (int index = 0; index < 100; index++)
        {
            throttle.Queue();
        }

        await Task.Delay(TimeSpan.FromMilliseconds(30));
        Assert.AreEqual(1, Volatile.Read(ref publishCount));
        await Task.Delay(TimeSpan.FromMilliseconds(250));
        Assert.AreEqual(2, Volatile.Read(ref publishCount));
    }

    [TestMethod]
    public async Task RequestAsyncCompletesAfterTheQueuedPublish()
    {
        int publishCount = 0;
        await using SnapshotPublishThrottle throttle = new SnapshotPublishThrottle(
            () => Interlocked.Increment(ref publishCount),
            CancellationToken.None,
            TimeSpan.FromMilliseconds(25));

        await throttle.RequestAsync().WaitAsync(TimeSpan.FromSeconds(2));

        Assert.AreEqual(1, Volatile.Read(ref publishCount));
    }
}

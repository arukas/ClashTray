using ClashTray.Contracts;

namespace ClashTray.Core.Tests;

[TestClass]
public sealed class SubscriptionSchedulerTests
{
    [TestMethod]
    public async Task SchedulerReportsProfileFailureAndKeepsRunning()
    {
        TaskCompletionSource<Exception> failure = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<bool> secondDelayStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        ConfigurationProfile profile = new ConfigurationProfile(
            "subscription",
            "测试订阅",
            Path.Combine(Path.GetTempPath(), "subscription.yaml"),
            new Uri("https://example.com/subscription.yaml"),
            null,
            false);
        int delayCount = 0;

        await using SubscriptionScheduler scheduler = new SubscriptionScheduler(
            _ => Task.FromResult<IReadOnlyList<ConfigurationProfile>>([profile]),
            (_, _) => Task.FromException(new HttpRequestException("订阅不可用")),
            () => new AppSettings(),
            (_, exception) => failure.TrySetResult(exception),
            null,
            (_, cancellationToken) =>
            {
                if (Interlocked.Increment(ref delayCount) == 1)
                {
                    return Task.CompletedTask;
                }

                secondDelayStarted.TrySetResult(true);
                return Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            });

        scheduler.Start();
        Exception exception = await failure.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await secondDelayStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.AreEqual(typeof(HttpRequestException), exception.GetType());
        Assert.AreEqual("订阅不可用", exception.Message);
        Assert.IsTrue(delayCount >= 2);
    }
}

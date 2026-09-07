using ClashTray.Contracts;
using ClashTray.Core;

namespace ClashTray.Core.Tests;

[TestClass]
public sealed class SubscriptionSchedulerTests
{
    [TestMethod]
    public async Task SchedulerReportsProfileFailureAndKeepsRunning()
    {
        var failure = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        var profile = new ConfigurationProfile(
            "subscription",
            "测试订阅",
            Path.Combine(Path.GetTempPath(), "subscription.yaml"),
            new Uri("https://example.com/subscription.yaml"),
            null,
            false);
        var delayCount = 0;

        await using var scheduler = new SubscriptionScheduler(
            _ => Task.FromResult<IReadOnlyList<ConfigurationProfile>>([profile]),
            (_, _) => Task.FromException(new HttpRequestException("订阅不可用")),
            () => new AppSettings(),
            (_, exception) => failure.TrySetResult(exception),
            null,
            (_, cancellationToken) => Interlocked.Increment(ref delayCount) == 1
                ? Task.CompletedTask
                : Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken));

        scheduler.Start();
        var exception = await failure.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.AreEqual(typeof(HttpRequestException), exception.GetType());
        Assert.AreEqual("订阅不可用", exception.Message);
        Assert.IsTrue(delayCount >= 2);
    }
}

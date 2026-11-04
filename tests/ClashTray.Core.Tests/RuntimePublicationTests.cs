using ClashTray.Contracts;
using ClashTray.Testing;

namespace ClashTray.Core.Tests;

[TestClass]
public sealed class RuntimePublicationTests
{
    [TestMethod]
    public async Task ReentrantLegacySubscriberCannotDeliverAnOlderAppSnapshotLast()
    {
        string root = TestFixtureDirectory.Create();
        await TestFixtureDirectory.RunAsync(root, async () =>
        {
            await using ClashTrayRuntime runtime = CreateRuntime(root);
            List<CoreState> delivered = [];
            bool reentered = false;
            runtime.SnapshotChanged += (_, _) =>
            {
                if (!reentered)
                {
                    reentered = true;
                    runtime.SetCoreStateForTesting(CoreState.Stopped);
                }
            };
            runtime.AppSnapshotChanged += (_, snapshot) => delivered.Add(snapshot.LocalDevice.CoreState);

            runtime.SetCoreStateForTesting(CoreState.Validating);

            CollectionAssert.AreEqual(new[] { CoreState.Validating, CoreState.Stopped }, delivered);
            Assert.AreEqual(runtime.AppSnapshot.LocalDevice.CoreState, delivered[^1]);
        });
    }

    [TestMethod]
    public async Task ConcurrentPublisherReturnsWhileSubscriberIsBlockedAndNewestStateIsDeliveredLast()
    {
        string root = TestFixtureDirectory.Create();
        await TestFixtureDirectory.RunAsync(root, async () =>
        {
            await using ClashTrayRuntime runtime = CreateRuntime(root);
            List<CoreState> delivered = [];
            TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            using ManualResetEventSlim release = new();
            int count = 0;
            runtime.SnapshotChanged += (_, _) =>
            {
                if (Interlocked.Increment(ref count) == 1)
                {
                    entered.TrySetResult();
                    release.Wait();
                }
            };
            runtime.AppSnapshotChanged += (_, snapshot) => delivered.Add(snapshot.LocalDevice.CoreState);
            Task first = Task.Run(() => runtime.SetCoreStateForTesting(CoreState.Validating));
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
                await Task.Run(() => runtime.SetCoreStateForTesting(CoreState.Stopped)).WaitAsync(TimeSpan.FromSeconds(10));
            }
            finally
            {
                release.Set();
                await first.WaitAsync(TimeSpan.FromSeconds(10));
            }

            CollectionAssert.AreEqual(new[] { CoreState.Validating, CoreState.Stopped }, delivered);
        });
    }

    private static ClashTrayRuntime CreateRuntime(string root) => RuntimeTestBuilder.Create(
        new AppPaths(Path.Combine(root, "user"), Path.Combine(root, "service")));
}

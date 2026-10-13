using ClashTray.Contracts;

namespace ClashTray.Core.Tests;

[TestClass]
public sealed class RuntimeStateStoreTests
{
    [TestMethod]
    public async Task ConcurrentReducersCommitBothFactsWithMonotonicRevisions()
    {
        RuntimeStateStore store = new(CreateSnapshot());
        TaskCompletionSource<bool> ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<bool> release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        Task coreUpdate = Task.Run(async () =>
        {
            ready.TrySetResult(true);
            await release.Task;
            store.Update(snapshot => snapshot with
            {
                Core = snapshot.Core with { State = CoreState.Running }
            });
        });
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Task tunUpdate = Task.Run(() =>
            store.Update(snapshot => snapshot with { Tun = TunState.Off }));
        release.TrySetResult(true);
        await Task.WhenAll(coreUpdate, tunUpdate);

        Assert.AreEqual(CoreState.Running, store.Snapshot.Core.State);
        Assert.AreEqual(TunState.Off, store.Snapshot.Tun);
        Assert.AreEqual(2, store.Revision);
    }

    [TestMethod]
    public void ConditionalCommitRejectsStaleFactWithoutChangingOtherFields()
    {
        RuntimeStateStore store = new(CreateSnapshot());
        long acceptedRevision = store.Revision;
        store.Update(snapshot => snapshot with { Tun = TunState.On });

        bool committed = store.TryUpdate(
            acceptedRevision,
            snapshot => snapshot with
            {
                Core = snapshot.Core with { State = CoreState.Running }
            },
            out long rejectedRevision);

        Assert.IsFalse(committed);
        Assert.AreEqual(1, rejectedRevision);
        Assert.AreEqual(CoreState.Stopped, store.Snapshot.Core.State);
        Assert.AreEqual(TunState.On, store.Snapshot.Tun);
        Assert.AreEqual(1, store.Revision);
    }

    private static RuntimeSnapshot CreateSnapshot() =>
        new(
            new CoreStatus(
                CoreState.Stopped,
                null,
                null,
                ProxyMode.Rule,
                0,
                0,
                0,
                0,
                0,
                0,
                null),
            SystemProxyState.Off,
            TunState.Unavailable,
            SubscriptionState.Idle,
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            null);
}

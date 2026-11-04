namespace ClashTray.Core.Tests;

[TestClass]
public sealed class SnapshotPublicationQueueTests
{
    [TestMethod]
    public void ReentrantBurstCoalescesButAppOnlyPublicationRetainsPendingLocalNotification()
    {
        SnapshotPublicationQueue publisher = new(new object());
        List<string> delivered = [];
        publisher.Publish(() => new(
            () =>
            {
                publisher.Publish(() => new(() => delivered.Add("local-2"), () => delivered.Add("app-2")));
                publisher.Publish(() => new(null, () => delivered.Add("app-3")));
                publisher.Publish(() => new(null, () => delivered.Add("app-4")));
            },
            () => delivered.Add("app-1")));

        string[] expected = ["app-1", "local-2", "app-4"];
        CollectionAssert.AreEqual(expected, delivered);
    }

    [TestMethod]
    public void SubscriberFailureIsRethrownAfterPendingStateDeliveryAndDoesNotOrphanFuturePublications()
    {
        SnapshotPublicationQueue publisher = new(new object());
        List<string> delivered = [];
        InvalidOperationException expected = new("synthetic subscriber failure");
        InvalidOperationException actual = Assert.ThrowsExactly<InvalidOperationException>(() =>
            publisher.Publish(() => new(
                () =>
                {
                    publisher.Publish(() => new(null, () => delivered.Add("app-2")));
                    throw expected;
                },
                () => delivered.Add("app-1"))));
        publisher.Publish(() => new(null, () => delivered.Add("app-3")));

        Assert.AreSame(expected, actual);
        string[] expectedDelivery = ["app-1", "app-2", "app-3"];
        CollectionAssert.AreEqual(expectedDelivery, delivered);
    }
}

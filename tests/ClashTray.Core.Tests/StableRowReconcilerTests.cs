using System.Globalization;
using System.Diagnostics;
using ClashTray.Contracts;

namespace ClashTray.Core.Tests;

[TestClass]
public sealed class StableRowReconcilerTests
{
    private static readonly string[] ExpectedUploadOrder = ["second", "third", "first"];
    [TestMethod]
    public void SameConnectionIdentityUpdatesInPlaceAndDeletionRemovesSelectionTarget()
    {
        StableRowReconciler<string, ConnectionInfo, TestConnectionRow> rows = new(item => item.Id);
        ConnectionInfo original = Connection("same", 10, "first");
        StableRowReconcileResult added = rows.Reconcile(
            [original],
            ["same"],
            item => new TestConnectionRow(item),
            (row, item) => row.Update(item));

        TestConnectionRow selected = rows.Rows.Single();
        ConnectionInfo changed = original with { UploadBytes = 99, Destination = "updated" };
        StableRowReconcileResult updated = rows.Reconcile(
            [changed],
            ["same"],
            item => new TestConnectionRow(item),
            (row, item) => row.Update(item));

        Assert.AreEqual(1, added.CreatedRows);
        Assert.AreSame(selected, rows.Rows.Single());
        Assert.AreEqual("same", selected.Item.Id);
        Assert.AreEqual("updated", selected.Item.Destination);
        Assert.AreEqual(1, updated.UpdatedRows);

        StableRowReconcileResult removed = rows.Reconcile(
            [],
            [],
            item => new TestConnectionRow(item),
            (row, item) => row.Update(item));
        Assert.AreEqual(1, removed.RemovedRows);
        Assert.AreEqual(0, rows.Rows.Count);
        Assert.AreEqual(0, rows.CachedRowCount);
    }

    [TestMethod]
    public void RepeatedSnapshotDoesNotNotifyAndConnectionProjectionKeepsSearchAndSortSemantics()
    {
        ConnectionInfo first = Connection("first", 5, "alpha.example");
        ConnectionInfo second = Connection("second", 30, "beta.example");
        ConnectionInfo third = Connection("third", 10, "beta.example");
        ConnectionInfo[] source = [first, second, third];
        StableRowReconciler<string, ConnectionInfo, TestConnectionRow> rows = new(item => item.Id);
        rows.Reconcile(
            source,
            ["first", "second", "third"],
            item => new TestConnectionRow(item),
            (row, item) => row.Update(item));
        int notifications = 0;
        rows.Rows.CollectionChanged += (_, _) => notifications++;

        IReadOnlyList<ConnectionInfo> sorted = RuntimeListProjection.FilterAndSortConnections(
            source,
            " beta ",
            "upload");
        StableRowReconcileResult repeated = rows.Reconcile(
            source,
            sorted.Select(item => item.Id).ToArray(),
            item => new TestConnectionRow(item),
            (row, item) => row.Update(item));

        Assert.AreEqual(2, sorted.Count);
        Assert.AreEqual("second", sorted[0].Id);
        Assert.AreEqual("third", sorted[1].Id);
        Assert.AreEqual(0, repeated.CreatedRows);
        Assert.AreEqual(0, repeated.UpdatedRows);
        Assert.AreEqual(0, repeated.MovedInView);
        Assert.IsTrue(notifications > 0);

        IReadOnlyList<ConnectionInfo> reordered = RuntimeListProjection.FilterAndSortConnections(
            source,
            null,
            "upload");
        StableRowReconciler<string, ConnectionInfo, TestConnectionRow> moveRows = new(item => item.Id);
        moveRows.Reconcile(
            source,
            source.Select(item => item.Id).ToArray(),
            item => new TestConnectionRow(item),
            (row, item) => row.Update(item));
        StableRowReconcileResult reorderResult = moveRows.Reconcile(
            source,
            reordered.Select(item => item.Id).ToArray(),
            item => new TestConnectionRow(item),
            (row, item) => row.Update(item));
        CollectionAssert.AreEqual(
            ExpectedUploadOrder,
            moveRows.Rows.Select(row => row.Item.Id).ToArray());
        Assert.IsTrue(reorderResult.MovedInView > 0);

        notifications = 0;
        StableRowReconcileResult unchanged = rows.Reconcile(
            source,
            sorted.Select(item => item.Id).ToArray(),
            item => new TestConnectionRow(item),
            (row, item) => row.Update(item));        Assert.AreEqual(0, unchanged.CreatedRows);
        Assert.AreEqual(0, unchanged.UpdatedRows);
        Assert.AreEqual(0, unchanged.MovedInView);
        Assert.AreEqual(0, notifications);
    }

    [TestMethod]
    public void LogSequenceTracksFoldClearEvictionAndIdenticalLaterEvents()
    {
        DateTimeOffset timestamp = DateTimeOffset.UnixEpoch;
        BoundedLogBuffer buffer = new(2);
        buffer.Add(new LogEntry(timestamp, "mihomo", "info", "same"));
        long firstSequence = buffer.Snapshot()[0].Sequence;
        buffer.Add(new LogEntry(timestamp, "mihomo", "info", "same"));

        Assert.AreEqual(firstSequence, buffer.Snapshot()[0].Sequence);
        Assert.AreEqual(2, buffer.Snapshot()[0].RepeatCount);

        buffer.Add(new LogEntry(timestamp, "mihomo", "warning", "middle"));
        buffer.Add(new LogEntry(timestamp, "mihomo", "info", "same"));
        IReadOnlyList<LogEntry> afterEviction = buffer.Snapshot();

        Assert.AreEqual(2, afterEviction.Count);
        Assert.AreEqual("middle", afterEviction[0].Message);
        Assert.AreEqual("same", afterEviction[1].Message);
        Assert.AreNotEqual(firstSequence, afterEviction[1].Sequence);
        Assert.AreEqual(timestamp, afterEviction[1].Timestamp);

        buffer.Clear();
        Assert.AreEqual(0, buffer.Snapshot().Count);
        buffer.Add(new LogEntry(timestamp, "mihomo", "info", "same"));
        Assert.AreNotEqual(firstSequence, buffer.Snapshot()[0].Sequence);
    }

    [TestMethod]
    public void SynchronousProjectionAppliesNewestInputAndEndpointSnapshotWithoutLateResults()
    {
        ConnectionInfo endpointA = Connection("a", 1, "a.example");
        ConnectionInfo endpointB = Connection("b", 2, "b.example");
        StableRowReconciler<string, ConnectionInfo, TestConnectionRow> rows = new(item => item.Id);

        IReadOnlyList<ConnectionInfo> firstProjection =
            RuntimeListProjection.FilterAndSortConnections([endpointA], "a.example", "newest");
        rows.Reconcile(
            [endpointA],
            firstProjection.Select(item => item.Id).ToArray(),
            item => new TestConnectionRow(item),
            (row, item) => row.Update(item));

        IReadOnlyList<ConnectionInfo> latestProjection =
            RuntimeListProjection.FilterAndSortConnections([endpointB], "b.example", "newest");
        rows.Reconcile(
            [endpointB],
            latestProjection.Select(item => item.Id).ToArray(),
            item => new TestConnectionRow(item),
            (row, item) => row.Update(item));

        Assert.AreEqual(1, rows.Rows.Count);
        Assert.AreEqual("b", rows.Rows[0].Item.Id);
        Assert.AreEqual("b.example", rows.Rows[0].Item.Destination);
    }

    [TestMethod]
    public void ThousandConnectionRowsBenchmarkComparesFullRebuildWithIncrementalUpdates()
    {
        const int rowCount = 1000;
        const int repetitions = 20;
        ConnectionInfo[] baselineSource = Enumerable.Range(0, rowCount)
            .Select(index => Connection(index.ToString(CultureInfo.InvariantCulture), index, $"host-{index}.example"))
            .ToArray();
        int baselineCreatedRows = 0;
        Stopwatch baselineTimer = Stopwatch.StartNew();
        for (int repeat = 0; repeat < repetitions; repeat++)
        {
            List<TestConnectionRow> rebuilt = baselineSource
                .Select(item => new TestConnectionRow(item))
                .ToList();
            baselineCreatedRows += rebuilt.Count;
        }

        baselineTimer.Stop();

        StableRowReconciler<string, ConnectionInfo, TestConnectionRow> rows = new(item => item.Id);
        rows.Reconcile(
            baselineSource,
            baselineSource.Select(item => item.Id).ToArray(),
            item => new TestConnectionRow(item),
            (row, item) => row.Update(item));
        int incrementalCreatedRows = 0;
        Stopwatch incrementalTimer = Stopwatch.StartNew();
        for (int repeat = 0; repeat < repetitions; repeat++)
        {
            ConnectionInfo[] updated = baselineSource
                .Select(item => item with { UploadBytes = item.UploadBytes + repeat + 1 })
                .ToArray();
            StableRowReconcileResult result = rows.Reconcile(
                updated,
                updated.Select(item => item.Id).ToArray(),
                item => new TestConnectionRow(item),
                (row, item) => row.Update(item));
            incrementalCreatedRows += result.CreatedRows;
        }

        incrementalTimer.Stop();
        Console.WriteLine(
            $"S4-T5 1000 rows x {repetitions}: rebuild {baselineTimer.Elapsed.TotalMilliseconds:F2} ms / {baselineCreatedRows} rows created; incremental {incrementalTimer.Elapsed.TotalMilliseconds:F2} ms / {incrementalCreatedRows} rows created.");

        Assert.AreEqual(rowCount * repetitions, baselineCreatedRows);
        Assert.AreEqual(0, incrementalCreatedRows);
        Assert.AreEqual(rowCount, rows.Rows.Count);
    }

    [TestMethod]
    public void LogProjectionPreservesSourceLevelAndTextFilterBehavior()
    {
        LogEntry[] logs =
        [
            new(DateTimeOffset.UnixEpoch, "mihomo", "error", "connection failed", Sequence: 11),
            new(DateTimeOffset.UnixEpoch, "ClashTray", "warning", "connection retry", Sequence: 12),
            new(DateTimeOffset.UnixEpoch, "mihomo", "info", "ready", Sequence: 13)
        ];

        IReadOnlyList<LogEntry> filtered = RuntimeListProjection.FilterLogs(
            logs,
            "connection",
            "warning",
            "ClashTray");

        Assert.AreEqual(1, filtered.Count);
        Assert.AreEqual(12, filtered[0].Sequence);
    }

    private static ConnectionInfo Connection(string id, long uploadBytes, string destination) =>
        new(
            id,
            "tcp",
            "127.0.0.1:1234",
            destination,
            "MATCH",
            "DIRECT",
            uploadBytes,
            0,
            DateTimeOffset.UnixEpoch);

    private sealed class TestConnectionRow(ConnectionInfo item)
    {
        public ConnectionInfo Item { get; private set; } = item;

        public bool Update(ConnectionInfo value)
        {
            if (Item == value)
            {
                return false;
            }

            Item = value;
            return true;
        }
    }
}
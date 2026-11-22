using ClashTray.App;

namespace ClashTray.Core.Tests;

[TestClass]
public sealed class TrafficSampleBufferTests
{
    [TestMethod]
    public void FirstSampleIsImmediateAndSubsecondSampleIsCoalesced()
    {
        ManualClock clock = new ManualClock();
        TrafficSampleBuffer buffer = new TrafficSampleBuffer(clock);

        Assert.IsTrue(buffer.TryAdd(10, 20));
        Assert.IsFalse(buffer.TryAdd(30, 40));
        Assert.AreEqual(1, buffer.Count);
        Assert.AreEqual(new TrafficSample(10, 20), buffer.Samples[0]);

        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.IsTrue(buffer.TryAdd(30, 40));
        Assert.AreEqual(new TrafficSample(30, 40), buffer.Samples[1]);
    }

    [TestMethod]
    public void BufferRetainsOnlyTheMostRecentSixtySamples()
    {
        ManualClock clock = new ManualClock();
        TrafficSampleBuffer buffer = new TrafficSampleBuffer(clock);
        for (int value = 0; value <= TrafficSampleBuffer.Capacity; value++)
        {
            Assert.IsTrue(buffer.TryAdd(value, value * 2));
            clock.Advance(TimeSpan.FromSeconds(1));
        }

        Assert.AreEqual(TrafficSampleBuffer.Capacity, buffer.Count);
        Assert.AreEqual(new TrafficSample(1, 2), buffer.Samples[0]);
        Assert.AreEqual(new TrafficSample(60, 120), buffer.Samples[^1]);
    }

    [TestMethod]
    public void UtcRollbackDoesNotDelayMonotonicSampling()
    {
        ManualClock clock = new ManualClock();
        TrafficSampleBuffer buffer = new TrafficSampleBuffer(clock);
        Assert.IsTrue(buffer.TryAdd(10, 20));

        clock.AdvanceTimestamp(TimeSpan.FromSeconds(1));
        clock.SetUtcNow(new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero));
        Assert.IsTrue(buffer.TryAdd(30, 40));
    }

    [TestMethod]
    public void ResetClearsHistoryAndAllowsAnImmediateSample()
    {
        ManualClock clock = new ManualClock();
        TrafficSampleBuffer buffer = new TrafficSampleBuffer(clock);
        Assert.IsTrue(buffer.TryAdd(10, 20));

        buffer.Reset();

        Assert.AreEqual(0, buffer.Count);
        Assert.IsTrue(buffer.TryAdd(30, 40));
        Assert.AreEqual(new TrafficSample(30, 40), buffer.Samples[0]);
    }

    private sealed class ManualClock : TimeProvider
    {
        private long _timestamp;
        private DateTimeOffset _utcNow = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public override long GetTimestamp() => _timestamp;

        public void Advance(TimeSpan interval)
        {
            AdvanceTimestamp(interval);
            _utcNow += interval;
        }

        public void AdvanceTimestamp(TimeSpan interval) => _timestamp += interval.Ticks;

        public void SetUtcNow(DateTimeOffset value) => _utcNow = value;
    }
}

namespace ClashTray.App;

internal readonly record struct TrafficSample(double UploadBytesPerSecond, double DownloadBytesPerSecond);

internal sealed class TrafficSampleBuffer
{
    public const int Capacity = 60;
    private static readonly TimeSpan SampleInterval = TimeSpan.FromSeconds(1);
    private readonly TimeProvider _timeProvider;
    private readonly Queue<TrafficSample> _samples = new();
    private long? _lastTimestamp;

    public TrafficSampleBuffer(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public int Count => _samples.Count;

    public IReadOnlyList<TrafficSample> Samples => _samples.ToArray();

    public bool TryAdd(double uploadBytesPerSecond, double downloadBytesPerSecond)
    {
        long timestamp = _timeProvider.GetTimestamp();
        if (_lastTimestamp is long previous
            && _timeProvider.GetElapsedTime(previous, timestamp) < SampleInterval)
        {
            return false;
        }

        _lastTimestamp = timestamp;
        _samples.Enqueue(new TrafficSample(uploadBytesPerSecond, downloadBytesPerSecond));
        while (_samples.Count > Capacity)
        {
            _samples.Dequeue();
        }

        return true;
    }

    public void Reset()
    {
        _samples.Clear();
        _lastTimestamp = null;
    }
}

using System.Net;
using ClashTray.Contracts;

namespace ClashTray.Core.Tests;

[TestClass]
public sealed class SubscriptionDownloadDeadlineTests
{
    [TestMethod]
    public async Task BodyDeadlinePreservesProfileDisposesStreamAndAllowsNextDownload()
    {
        string root = Path.Combine(Path.GetTempPath(), "ClashTrayTests", Guid.NewGuid().ToString("N"));
        using BodyHandler handler = new();
        using DeadlineClock clock = new();
        try
        {
            AppPaths paths = new(Path.Combine(root, "local"), Path.Combine(root, "program"));
            ConfigurationStore store = new(paths, handler, null, TimeSpan.FromSeconds(30), clock);
            Uri uri = new("https://subscription.invalid/config");
            ConfigurationProfile original = await store.ImportSubscriptionAsync(uri);
            string content = await File.ReadAllTextAsync(original.Path);
            string metadataPath = Path.Combine(paths.ConfigurationsRoot, $"{original.Id}.json");
            string metadata = await File.ReadAllTextAsync(metadataPath);
            using StalledBody stream = new();
            handler.Body = stream;
            using CancellationTokenSource watchdog = new(TimeSpan.FromSeconds(5));
            Task<ConfigurationProfile> download = store.ImportSubscriptionAsync(uri, cancellationToken: watchdog.Token);
            await stream.ReadStarted.Task.WaitAsync(watchdog.Token);
            clock.Expire();
            await Assert.ThrowsExactlyAsync<TimeoutException>(() => download);
            Assert.IsTrue(stream.WasDisposed);
            Assert.AreEqual(content, await File.ReadAllTextAsync(original.Path));
            Assert.AreEqual(metadata, await File.ReadAllTextAsync(metadataPath));
            handler.Body = null;
            Assert.AreEqual(original.Id, (await store.ImportSubscriptionAsync(uri)).Id);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task CallerCancellationRemainsCancellationAndDisposesBody()
    {
        string root = Path.Combine(Path.GetTempPath(), "ClashTrayTests", Guid.NewGuid().ToString("N"));
        using StalledBody stream = new();
        using BodyHandler handler = new() { Body = stream };
        using CancellationTokenSource caller = new();
        try
        {
            ConfigurationStore store = new(new AppPaths(Path.Combine(root, "local"), Path.Combine(root, "program")), handler);
            Task<ConfigurationProfile> download = store.ImportSubscriptionAsync(new Uri("https://subscription.invalid/config"), cancellationToken: caller.Token);
            await stream.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await caller.CancelAsync();
            await Assert.ThrowsAsync<OperationCanceledException>(() => download);
            Assert.IsTrue(stream.WasDisposed);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class BodyHandler : HttpMessageHandler
    {
        public Stream? Body { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = Body is null ? new StringContent("mixed-port: 7890\n") : new StreamContent(Body)
            });
        }
    }

    private sealed class StalledBody : Stream
    {
        private bool _prefixRead;
        public TaskCompletionSource ReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool WasDisposed { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!_prefixRead)
            {
                _prefixRead = true;
                "mixed-port: 7890\n"u8.CopyTo(buffer.Span);
                return 17;
            }

            ReadStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }

        protected override void Dispose(bool disposing) { WasDisposed = true; base.Dispose(disposing); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class DeadlineClock : TimeProvider, IDisposable
    {
        private DeadlineTimer? _timer;
        public void Dispose() => _timer?.Dispose();
        public void Expire() => _timer!.Fire();
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Assert.AreEqual(TimeSpan.FromSeconds(30), dueTime);
            _timer = new DeadlineTimer(callback, state);
            return _timer;
        }

        private sealed class DeadlineTimer(TimerCallback callback, object? state) : ITimer
        {
            private bool _disposed;
            public void Fire() { if (!_disposed) { callback(state); } }
            public bool Change(TimeSpan dueTime, TimeSpan period) => !_disposed;
            public void Dispose() => _disposed = true;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}

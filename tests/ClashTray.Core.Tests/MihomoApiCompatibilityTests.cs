using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using ClashTray.Core;

namespace ClashTray.Core.Tests;

[TestClass]
public sealed class MihomoApiCompatibilityTests
{
    private static readonly string[] ExpectedProxyMembers = ["Node"];

    [TestMethod]
    public async Task TrafficReturnsFirstJsonLineWithoutWaitingForStreamEof()
    {
        var stream = new ChunkedStream(
            Encoding.UTF8.GetBytes("{\"upTotal\":4,\"downTotal\":8,\"up\":1,\"down\":2}\n{\"up\":9}\n"),
            chunkSize: 3,
            holdOpen: true);
        using var handler = new StreamingHandler(stream);
        using var httpClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
        var api = new MihomoApiClient(
            httpClient,
            new Uri("http://127.0.0.1:9090/"),
            "test-secret",
            streamingFirstRecordTimeout: TimeSpan.FromSeconds(1),
            maxStreamingRecordBytes: 1024);

        using var document = await api.GetTrafficAsync();

        Assert.AreEqual(4, document.RootElement.GetProperty("upTotal").GetInt64());
        Assert.AreEqual(8, document.RootElement.GetProperty("downTotal").GetInt64());
        Assert.IsTrue(stream.Disposed);
    }

    [TestMethod]
    public async Task MemoryHandlesFragmentsMultipleLinesAndCrLf()
    {
        var stream = new ChunkedStream(
            Encoding.UTF8.GetBytes("\r\n{\"inuse\":4096}\r\n{\"inuse\":8192}\r\n"),
            chunkSize: 2,
            holdOpen: true);
        using var handler = new StreamingHandler(stream);
        using var httpClient = new HttpClient(handler);
        var api = new MihomoApiClient(
            httpClient,
            new Uri("http://127.0.0.1:9090/"),
            "test-secret",
            streamingFirstRecordTimeout: TimeSpan.FromSeconds(1),
            maxStreamingRecordBytes: 1024);

        using var document = await api.GetMemoryAsync();

        Assert.AreEqual(4096, document.RootElement.GetProperty("inuse").GetInt64());
        Assert.IsTrue(stream.Disposed);
    }

    [TestMethod]
    public async Task EmptyStreamingResponseIsClassified()
    {
        var api = CreateStreamingApi(new ChunkedStream([], chunkSize: 4, holdOpen: false));

        var exception = await Assert.ThrowsExactlyAsync<MihomoStreamException>(() => api.GetTrafficAsync());

        Assert.AreEqual(MihomoStreamFailureKind.EmptyResponse, exception.Kind);
    }

    [TestMethod]
    public async Task StreamingDisconnectBeforeNewlineIsClassified()
    {
        var api = CreateStreamingApi(new ChunkedStream(
            Encoding.UTF8.GetBytes("{\"up\":1}"),
            chunkSize: 2,
            holdOpen: false));

        var exception = await Assert.ThrowsExactlyAsync<MihomoStreamException>(() => api.GetTrafficAsync());

        Assert.AreEqual(MihomoStreamFailureKind.DisconnectedBeforeRecord, exception.Kind);
    }

    [TestMethod]
    public async Task InvalidStreamingJsonIsClassified()
    {
        var api = CreateStreamingApi(new ChunkedStream(
            Encoding.UTF8.GetBytes("not-json\n"),
            chunkSize: 2,
            holdOpen: false));

        var exception = await Assert.ThrowsExactlyAsync<MihomoStreamException>(() => api.GetTrafficAsync());

        Assert.AreEqual(MihomoStreamFailureKind.InvalidJson, exception.Kind);
    }

    [TestMethod]
    public async Task OversizedStreamingRecordIsRejectedBeforeReadingUnboundedData()
    {
        var api = CreateStreamingApi(new ChunkedStream(
            Encoding.UTF8.GetBytes($"{{\"payload\":\"{new string('x', 32)}\"}}\n"),
            chunkSize: 3,
            holdOpen: true),
            maxStreamingRecordBytes: 16);

        var exception = await Assert.ThrowsExactlyAsync<MihomoStreamException>(() => api.GetTrafficAsync());

        Assert.AreEqual(MihomoStreamFailureKind.RecordTooLarge, exception.Kind);
    }

    [TestMethod]
    public async Task StreamingFirstRecordTimeoutDoesNotWaitForEof()
    {
        var stream = new ChunkedStream([], chunkSize: 4, holdOpen: true);
        var api = CreateStreamingApi(stream, streamingFirstRecordTimeout: TimeSpan.FromMilliseconds(80));

        var exception = await Assert.ThrowsExactlyAsync<TimeoutException>(() => api.GetTrafficAsync());

        StringAssert.Contains(exception.Message, "首条指标记录");
        Assert.IsTrue(stream.Disposed);
    }

    [TestMethod]
    public async Task StreamingCancellationIsNotConvertedToTimeout()
    {
        var stream = new ChunkedStream([], chunkSize: 4, holdOpen: true);
        var api = CreateStreamingApi(stream, streamingFirstRecordTimeout: TimeSpan.FromSeconds(5));
        using var cancellation = new CancellationTokenSource();
        var task = api.GetTrafficAsync(cancellation.Token);
        await Task.Delay(40);
        cancellation.Cancel();

        var canceled = false;
        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
            canceled = true;
        }

        Assert.IsTrue(canceled);
        Assert.IsTrue(stream.Disposed);
    }

    [TestMethod]
    [DataRow(401)]
    [DataRow(403)]
    [DataRow(500)]
    public async Task StreamingHttpErrorsRemainVisible(int statusCode)
    {
        using var handler = new StatusHandler((HttpStatusCode)statusCode);
        using var httpClient = new HttpClient(handler);
        var api = new MihomoApiClient(httpClient, new Uri("http://127.0.0.1:9090/"), "test-secret");

        var exception = await Assert.ThrowsExactlyAsync<HttpRequestException>(() => api.GetTrafficAsync());

        Assert.AreEqual((HttpStatusCode)statusCode, exception.StatusCode);
        StringAssert.Contains(exception.Message, statusCode.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    [TestMethod]
    [DataRow("test-secret", "Bearer test-secret")]
    [DataRow("", null)]
    public async Task FakeIpCacheFlushUsesPostAndOptionalAuthentication(string secret, string? expectedAuthorization)
    {
        var handler = new RecordingHandler();
        using var httpClient = new HttpClient(handler);
        var api = new MihomoApiClient(httpClient, new Uri("http://127.0.0.1:9090/"), secret);

        using var response = await api.ClearFakeIpCacheAsync();

        Assert.AreEqual(HttpMethod.Post, handler.Method);
        Assert.AreEqual("/cache/fakeip/flush", handler.PathAndQuery);
        Assert.AreEqual(expectedAuthorization, handler.Authorization);
    }

    [TestMethod]
    public void RulesParserReadsOfficialObjectShape()
    {
        using var document = JsonDocument.Parse("{\"rules\":[{\"type\":\"DOMAIN\",\"payload\":\"example.com\",\"proxy\":\"Proxy\",\"size\":-1}]} ");

        var rules = MihomoDataParser.ParseRules(document);

        Assert.AreEqual(1, rules.Count);
        Assert.AreEqual("DOMAIN", rules[0].Type);
        Assert.AreEqual("example.com", rules[0].Payload);
        Assert.AreEqual("Proxy", rules[0].Proxy);
        Assert.AreEqual(-1, rules[0].Size);
    }

    [TestMethod]
    public void WebSocketUriPreservesQueryParameters()
    {
        var api = new MihomoApiClient(
            new HttpClient(new RecordingHandler()),
            new Uri("http://127.0.0.1:9090/"),
            "test-secret");

        var uri = api.BuildWebSocketUri("/logs?level=debug&format=structured");

        Assert.AreEqual("ws", uri.Scheme);
        Assert.AreEqual("127.0.0.1", uri.Host);
        Assert.AreEqual(9090, uri.Port);
        Assert.AreEqual("/logs?level=debug&format=structured", uri.PathAndQuery);
    }

    [TestMethod]
    public void LogsParserReadsOfficialStructuredSingleMessage()
    {
        using var document = JsonDocument.Parse(
            "{\"time\":\"12:34:56\",\"level\":\"warning\",\"message\":\"controller warning\",\"fields\":[{\"key\":\"value\"}]}");

        var logs = MihomoDataParser.ParseLogs(document, "mihomo");

        Assert.AreEqual(1, logs.Count);
        Assert.AreEqual("warning", logs[0].Level);
        Assert.AreEqual("controller warning", logs[0].Message);
        Assert.AreEqual("mihomo", logs[0].Source);
    }

    [TestMethod]
    public void ConnectionsParserReadsOfficialRulePayload()
    {
        using var document = JsonDocument.Parse(
            "{\"connections\":[{\"id\":\"connection-1\",\"metadata\":{\"network\":\"tcp\",\"sourceIP\":\"127.0.0.1:1000\",\"destinationIP\":\"example.com:443\"},\"upload\":10,\"download\":20,\"start\":\"2026-09-07T12:34:56Z\",\"chains\":[\"Proxy\"],\"rule\":\"DOMAIN\",\"rulePayload\":\"example.com\"}]}");

        var connections = MihomoDataParser.ParseConnections(document);

        Assert.AreEqual(1, connections.Count);
        Assert.AreEqual("DOMAIN", connections[0].Rule);
        Assert.AreEqual("example.com", connections[0].RulePayload);
    }

    [TestMethod]
    public void ParserToleratesUnexpectedJsonShapes()
    {
        using var proxies = JsonDocument.Parse(
            "{\"proxies\":{\"Group\":{\"type\":\"Selector\",\"all\":[\"Node\",17,null]}}}");
        using var emptyLog = JsonDocument.Parse("{}");

        var proxyData = MihomoDataParser.ParseProxies(proxies);
        var logs = MihomoDataParser.ParseLogs(emptyLog, "mihomo");

        Assert.AreEqual(1, proxyData.Groups.Count);
        CollectionAssert.AreEqual(ExpectedProxyMembers, proxyData.Groups[0].Members.ToArray());
        Assert.AreEqual(0, logs.Count);
    }

    [TestMethod]
    public async Task ApiRejectsOversizedJsonResponse()
    {
        var api = new MihomoApiClient(
            new HttpClient(new OversizedResponseHandler()),
            new Uri("http://127.0.0.1:9090/"),
            "test-secret");

        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => api.GetVersionAsync());
    }

    [TestMethod]
    public void ParserBoundsConnectionAndRuleLists()
    {
        var connectionItems = string.Join(
            ",",
            Enumerable.Range(0, 2_100)
                .Select(index => $"{{\"id\":\"connection-{index}\",\"metadata\":{{\"network\":\"tcp\"}}}}"));
        var ruleItems = string.Join(
            ",",
            Enumerable.Range(0, 5_100)
                .Select(index => $"{{\"type\":\"DOMAIN\",\"payload\":\"example-{index}.com\",\"proxy\":\"Proxy\"}}"));

        using var connectionsDocument = JsonDocument.Parse($"{{\"connections\":[{connectionItems}]}}");
        using var rulesDocument = JsonDocument.Parse($"{{\"rules\":[{ruleItems}]}}");

        Assert.AreEqual(2_000, MihomoDataParser.ParseConnections(connectionsDocument).Count);
        Assert.AreEqual(5_000, MihomoDataParser.ParseRules(rulesDocument).Count);
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public HttpMethod? Method { get; private set; }

        public string? PathAndQuery { get; private set; }

        public string? Authorization { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Method = request.Method;
            PathAndQuery = request.RequestUri?.PathAndQuery;
            Authorization = request.Headers.Authorization?.ToString();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent)
            {
                Content = new StringContent(string.Empty)
            });
        }
    }

    private sealed class OversizedResponseHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(new byte[16 * 1024 * 1024 + 1])
            });
    }

    private static MihomoApiClient CreateStreamingApi(
        Stream stream,
        TimeSpan? streamingFirstRecordTimeout = null,
        int maxStreamingRecordBytes = 1024)
    {
        var handler = new StreamingHandler(stream);
        var httpClient = new HttpClient(handler);
        return new MihomoApiClient(
            httpClient,
            new Uri("http://127.0.0.1:9090/"),
            "test-secret",
            streamingFirstRecordTimeout ?? TimeSpan.FromSeconds(1),
            maxStreamingRecordBytes);
    }

    private sealed class StreamingHandler : HttpMessageHandler
    {
        private readonly Stream _stream;

        public StreamingHandler(Stream stream) => _stream = stream;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamingContent(_stream)
            });
    }

    private sealed class StatusHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _statusCode;

        public StatusHandler(HttpStatusCode statusCode) => _statusCode = statusCode;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(_statusCode)
            {
                Content = new StringContent("redacted")
            });
    }

    private sealed class StreamingContent : HttpContent
    {
        private readonly Stream _stream;

        public StreamingContent(Stream stream) => _stream = stream;

        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult(_stream);

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            _stream.CopyToAsync(stream);

        protected override bool TryComputeLength(out long length)
        {
            length = _stream.CanSeek ? _stream.Length : 0;
            return _stream.CanSeek;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _stream.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    private sealed class ChunkedStream : Stream
    {
        private readonly byte[] _data;
        private readonly int _chunkSize;
        private readonly bool _holdOpen;
        private int _position;

        public ChunkedStream(byte[] data, int chunkSize, bool holdOpen)
        {
            _data = data;
            _chunkSize = chunkSize;
            _holdOpen = holdOpen;
        }

        public bool Disposed { get; private set; }

        public override bool CanRead => !Disposed;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _data.Length;
        public override long Position { get => _position; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_position >= _data.Length)
            {
                return 0;
            }

            var length = Math.Min(Math.Min(count, _chunkSize), _data.Length - _position);
            Buffer.BlockCopy(_data, _position, buffer, offset, length);
            _position += length;
            return length;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var temporary = new byte[Math.Min(buffer.Length, _chunkSize)];
            var count = Read(temporary, 0, temporary.Length);
            if (count > 0)
            {
                temporary.AsMemory(0, count).CopyTo(buffer);
                await Task.Yield();
                return count;
            }

            if (_holdOpen)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            return 0;
        }

        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }
}

using System.Net;
using System.Text;
using System.Text.Json;
using ClashTray.Contracts;

namespace ClashTray.Core.Tests;

[TestClass]
public sealed class MihomoApiCompatibilityTests
{
    private static readonly string[] ExpectedProxyMembers = ["Node"];

    [TestMethod]
    public async Task TrafficReturnsFirstJsonLineWithoutWaitingForStreamEof()
    {
        ChunkedStream stream = new ChunkedStream(
            Encoding.UTF8.GetBytes("{\"upTotal\":4,\"downTotal\":8,\"up\":1,\"down\":2}\n{\"up\":9}\n"),
            chunkSize: 3,
            holdOpen: true);
        using StreamingHandler handler = new StreamingHandler(stream);
        using HttpClient httpClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
        MihomoApiClient api = new MihomoApiClient(
            httpClient,
            new Uri("http://127.0.0.1:9090/"),
            "test-secret",
            streamingFirstRecordTimeout: TimeSpan.FromSeconds(1),
            maxStreamingRecordBytes: 1024);

        using JsonDocument document = await api.GetTrafficAsync();

        Assert.AreEqual(4, document.RootElement.GetProperty("upTotal").GetInt64());
        Assert.AreEqual(8, document.RootElement.GetProperty("downTotal").GetInt64());
        Assert.IsTrue(stream.Disposed);
    }

    [TestMethod]
    public async Task MemoryHandlesFragmentsMultipleLinesAndCrLf()
    {
        ChunkedStream stream = new ChunkedStream(
            Encoding.UTF8.GetBytes("\r\n{\"inuse\":4096}\r\n{\"inuse\":8192}\r\n"),
            chunkSize: 2,
            holdOpen: true);
        using StreamingHandler handler = new StreamingHandler(stream);
        using HttpClient httpClient = new HttpClient(handler);
        MihomoApiClient api = new MihomoApiClient(
            httpClient,
            new Uri("http://127.0.0.1:9090/"),
            "test-secret",
            streamingFirstRecordTimeout: TimeSpan.FromSeconds(1),
            maxStreamingRecordBytes: 1024);

        using JsonDocument document = await api.GetMemoryAsync();

        Assert.AreEqual(4096, document.RootElement.GetProperty("inuse").GetInt64());
        Assert.IsTrue(stream.Disposed);
    }

    [TestMethod]
    public async Task EmptyStreamingResponseIsClassified()
    {
        using StreamingApiContext context = CreateStreamingApi(new ChunkedStream([], chunkSize: 4, holdOpen: false));
        MihomoApiClient api = context.Api;

        MihomoStreamException exception = await Assert.ThrowsExactlyAsync<MihomoStreamException>(() => api.GetTrafficAsync());

        Assert.AreEqual(MihomoStreamFailureKind.EmptyResponse, exception.Kind);
    }

    [TestMethod]
    public async Task StreamingDisconnectBeforeNewlineIsClassified()
    {
        using StreamingApiContext context = CreateStreamingApi(new ChunkedStream(
            Encoding.UTF8.GetBytes("{\"up\":1}"),
            chunkSize: 2,
            holdOpen: false));
        MihomoApiClient api = context.Api;

        MihomoStreamException exception = await Assert.ThrowsExactlyAsync<MihomoStreamException>(() => api.GetTrafficAsync());

        Assert.AreEqual(MihomoStreamFailureKind.DisconnectedBeforeRecord, exception.Kind);
    }

    [TestMethod]
    public async Task InvalidStreamingJsonIsClassified()
    {
        using StreamingApiContext context = CreateStreamingApi(new ChunkedStream(
            Encoding.UTF8.GetBytes("not-json\n"),
            chunkSize: 2,
            holdOpen: false));
        MihomoApiClient api = context.Api;

        MihomoStreamException exception = await Assert.ThrowsExactlyAsync<MihomoStreamException>(() => api.GetTrafficAsync());

        Assert.AreEqual(MihomoStreamFailureKind.InvalidJson, exception.Kind);
    }

    [TestMethod]
    public async Task OversizedStreamingRecordIsRejectedBeforeReadingUnboundedData()
    {
        using StreamingApiContext context = CreateStreamingApi(new ChunkedStream(
            Encoding.UTF8.GetBytes($"{{\"payload\":\"{new string('x', 32)}\"}}\n"),
            chunkSize: 3,
            holdOpen: true),
            maxStreamingRecordBytes: 16);
        MihomoApiClient api = context.Api;

        MihomoStreamException exception = await Assert.ThrowsExactlyAsync<MihomoStreamException>(() => api.GetTrafficAsync());

        Assert.AreEqual(MihomoStreamFailureKind.RecordTooLarge, exception.Kind);
    }

    [TestMethod]
    public async Task StreamingFirstRecordTimeoutDoesNotWaitForEof()
    {
        ChunkedStream stream = new ChunkedStream([], chunkSize: 4, holdOpen: true);
        using StreamingApiContext context = CreateStreamingApi(stream, streamingFirstRecordTimeout: TimeSpan.FromMilliseconds(80));
        MihomoApiClient api = context.Api;

        TimeoutException exception = await Assert.ThrowsExactlyAsync<TimeoutException>(() => api.GetTrafficAsync());

        StringAssert.Contains(exception.Message, "首条指标记录", StringComparison.Ordinal);
        Assert.IsTrue(stream.Disposed);
    }

    [TestMethod]
    public async Task StreamingCancellationIsNotConvertedToTimeout()
    {
        ChunkedStream stream = new ChunkedStream([], chunkSize: 4, holdOpen: true);
        using StreamingApiContext context = CreateStreamingApi(stream, streamingFirstRecordTimeout: TimeSpan.FromSeconds(5));
        MihomoApiClient api = context.Api;
        using CancellationTokenSource cancellation = new CancellationTokenSource();
        Task<JsonDocument> task = api.GetTrafficAsync(cancellation.Token);
        await Task.Delay(40);
        await cancellation.CancelAsync();

        bool canceled = false;
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
        using StatusHandler handler = new StatusHandler((HttpStatusCode)statusCode);
        using HttpClient httpClient = new HttpClient(handler);
        MihomoApiClient api = new MihomoApiClient(httpClient, new Uri("http://127.0.0.1:9090/"), "test-secret");

        HttpRequestException exception = await Assert.ThrowsExactlyAsync<HttpRequestException>(() => api.GetTrafficAsync());

        Assert.AreEqual((HttpStatusCode)statusCode, exception.StatusCode);
        StringAssert.Contains(exception.Message, statusCode.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);
    }

    [TestMethod]
    [DataRow("test-secret", "Bearer test-secret")]
    [DataRow("", null)]
    public async Task FakeIpCacheFlushUsesPostAndOptionalAuthentication(string secret, string? expectedAuthorization)
    {
        using RecordingHandler handler = new RecordingHandler();
        using HttpClient httpClient = new HttpClient(handler, disposeHandler: false);
        MihomoApiClient api = new MihomoApiClient(httpClient, new Uri("http://127.0.0.1:9090/"), secret);

        using JsonDocument response = await api.ClearFakeIpCacheAsync();

        Assert.AreEqual(HttpMethod.Post, handler.Method);
        Assert.AreEqual("/cache/fakeip/flush", handler.PathAndQuery);
        Assert.AreEqual(expectedAuthorization, handler.Authorization);
    }

    [TestMethod]
    public void RulesParserReadsOfficialObjectShape()
    {
        using JsonDocument document = JsonDocument.Parse("{\"rules\":[{\"type\":\"DOMAIN\",\"payload\":\"example.com\",\"proxy\":\"Proxy\",\"size\":-1}]} ");

        IReadOnlyList<RuleInfo> rules = MihomoDataParser.ParseRules(document);

        Assert.AreEqual(1, rules.Count);
        Assert.AreEqual("DOMAIN", rules[0].Type);
        Assert.AreEqual("example.com", rules[0].Payload);
        Assert.AreEqual("Proxy", rules[0].Proxy);
        Assert.AreEqual(-1, rules[0].Size);
    }

    [TestMethod]
    public void WebSocketUriPreservesQueryParameters()
    {
        using RecordingHandler websocketHandler = new RecordingHandler();
        using HttpClient websocketClient = new HttpClient(websocketHandler, disposeHandler: false);
        MihomoApiClient api = new MihomoApiClient(
            websocketClient,
            new Uri("http://127.0.0.1:9090/"),
            "test-secret");

        Uri uri = api.BuildWebSocketUri("/logs?level=debug&format=structured");

        Assert.AreEqual("ws", uri.Scheme);
        Assert.AreEqual("127.0.0.1", uri.Host);
        Assert.AreEqual(9090, uri.Port);
        Assert.AreEqual("/logs?level=debug&format=structured", uri.PathAndQuery);
    }

    [TestMethod]
    public void LogsParserReadsOfficialStructuredSingleMessage()
    {
        using JsonDocument document = JsonDocument.Parse(
            "{\"time\":\"12:34:56\",\"level\":\"warning\",\"message\":\"controller warning\",\"fields\":[{\"key\":\"value\"}]}");

        IReadOnlyList<LogEntry> logs = MihomoDataParser.ParseLogs(document, "mihomo");

        Assert.AreEqual(1, logs.Count);
        Assert.AreEqual("warning", logs[0].Level);
        Assert.AreEqual("controller warning", logs[0].Message);
        Assert.AreEqual("mihomo", logs[0].Source);
    }

    [TestMethod]
    public void ConnectionsParserReadsOfficialRulePayload()
    {
        using JsonDocument document = JsonDocument.Parse(
            "{\"connections\":[{\"id\":\"connection-1\",\"metadata\":{\"network\":\"tcp\",\"sourceIP\":\"127.0.0.1:1000\",\"destinationIP\":\"example.com:443\"},\"upload\":10,\"download\":20,\"start\":\"2026-09-07T12:34:56Z\",\"chains\":[\"Proxy\"],\"rule\":\"DOMAIN\",\"rulePayload\":\"example.com\"}]}");

        IReadOnlyList<ConnectionInfo> connections = MihomoDataParser.ParseConnections(document);

        Assert.AreEqual(1, connections.Count);
        Assert.AreEqual("DOMAIN", connections[0].Rule);
        Assert.AreEqual("example.com", connections[0].RulePayload);
    }

    [TestMethod]
    public void ParserToleratesUnexpectedJsonShapes()
    {
        using JsonDocument proxies = JsonDocument.Parse(
            "{\"proxies\":{\"Group\":{\"type\":\"Selector\",\"all\":[\"Node\",17,null]}}}");
        using JsonDocument emptyLog = JsonDocument.Parse("{}");

        (IReadOnlyList<ProxyGroup> Groups, IReadOnlyList<ProxyNode> Nodes) proxyData = MihomoDataParser.ParseProxies(proxies);
        IReadOnlyList<LogEntry> logs = MihomoDataParser.ParseLogs(emptyLog, "mihomo");

        Assert.AreEqual(1, proxyData.Groups.Count);
        CollectionAssert.AreEqual(ExpectedProxyMembers, proxyData.Groups[0].Members.ToArray());
        Assert.AreEqual(0, logs.Count);
    }

    [TestMethod]
    public async Task ApiRejectsOversizedJsonResponse()
    {
        using OversizedResponseHandler oversizedHandler = new OversizedResponseHandler();
        using HttpClient oversizedClient = new HttpClient(oversizedHandler, disposeHandler: false);
        MihomoApiClient api = new MihomoApiClient(
            oversizedClient,
            new Uri("http://127.0.0.1:9090/"),
            "test-secret");

        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => api.GetVersionAsync());
    }

    [TestMethod]
    public void ParserBoundsConnectionAndRuleLists()
    {
        string connectionItems = string.Join(
            ",",
            Enumerable.Range(0, 2_100)
                .Select(index => $"{{\"id\":\"connection-{index}\",\"metadata\":{{\"network\":\"tcp\"}}}}"));
        string ruleItems = string.Join(
            ",",
            Enumerable.Range(0, 5_100)
                .Select(index => $"{{\"type\":\"DOMAIN\",\"payload\":\"example-{index}.com\",\"proxy\":\"Proxy\"}}"));

        using JsonDocument connectionsDocument = JsonDocument.Parse($"{{\"connections\":[{connectionItems}]}}");
        using JsonDocument rulesDocument = JsonDocument.Parse($"{{\"rules\":[{ruleItems}]}}");

        Assert.AreEqual(2_000, MihomoDataParser.ParseConnections(connectionsDocument).Count);
        Assert.AreEqual(5_000, MihomoDataParser.ParseRules(rulesDocument).Count);
    }

    [TestMethod]
    public void ParserBoundsUntrustedDisplayFieldsAndIdentifiers()
    {
        string longIdentifier = new string('n', 300);
        string longText = new string('x', 5_000);
        using JsonDocument proxies = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            proxies = new Dictionary<string, object>
            {
                [longIdentifier] = new { type = "Direct" },
                ["Group"] = new { type = "Selector", all = new[] { longIdentifier, "Node" } }
            }
        }));
        using JsonDocument connections = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            connections = new object[]
            {
                new
                {
                    id = longIdentifier,
                    metadata = new { network = "tcp" }
                },
                new
                {
                    id = "connection-1",
                    metadata = new { network = longText, sourceIP = longText, destinationIP = longText },
                    chains = new[] { longText },
                    rule = longText,
                    rulePayload = longText
                }
            }
        }));
        using JsonDocument rules = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            rules = new object[]
            {
                new { type = "DOMAIN", payload = longText, proxy = longIdentifier },
                new object[] { "MATCH", longText, longIdentifier }
            }
        }));
        using JsonDocument providers = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            providers = new Dictionary<string, object>
            {
                [longIdentifier] = new { vehicleType = "HTTP" },
                ["Provider"] = new { message = longText }
            }
        }));
        using JsonDocument logs = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            logs = new[] { new { level = "warning", message = longText } }
        }));

        (IReadOnlyList<ProxyGroup> Groups, IReadOnlyList<ProxyNode> Nodes) proxyData =
            MihomoDataParser.ParseProxies(proxies);
        IReadOnlyList<ConnectionInfo> connectionData = MihomoDataParser.ParseConnections(connections);
        IReadOnlyList<RuleInfo> ruleData = MihomoDataParser.ParseRules(rules);
        IReadOnlyList<ProviderStatus> providerData = MihomoDataParser.ParseProviders(providers, "proxy");
        IReadOnlyList<LogEntry> logData = MihomoDataParser.ParseLogs(logs, "mihomo");

        Assert.AreEqual(1, proxyData.Groups.Count);
        CollectionAssert.AreEqual(ExpectedProxyMembers, proxyData.Groups[0].Members.ToArray());
        Assert.AreEqual(0, proxyData.Nodes.Count);
        Assert.AreEqual(1, connectionData.Count);
        Assert.IsTrue(connectionData[0].Network.Length <= 1_024);
        Assert.IsTrue(connectionData[0].RulePayload.Length <= 1_024);
        Assert.AreEqual(2, ruleData.Count);
        Assert.IsTrue(ruleData[0].Payload.Length <= 4_096);
        Assert.IsTrue(ruleData[1].Proxy.Length <= 256);
        Assert.AreEqual(1, providerData.Count);
        Assert.IsTrue(providerData[0].Error!.Length <= 1_024);
        Assert.AreEqual(1, logData.Count);
        Assert.IsTrue(logData[0].Message.Length <= 4_096);
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

    private static StreamingApiContext CreateStreamingApi(
        Stream stream,
        TimeSpan? streamingFirstRecordTimeout = null,
        int maxStreamingRecordBytes = 1024) =>
        new StreamingApiContext(
            stream,
            streamingFirstRecordTimeout ?? TimeSpan.FromSeconds(1),
            maxStreamingRecordBytes);

    private sealed class StreamingApiContext : IDisposable
    {
        private readonly StreamingHandler _handler;
        private readonly HttpClient _httpClient;

        public StreamingApiContext(Stream stream, TimeSpan streamingFirstRecordTimeout, int maxStreamingRecordBytes)
        {
            _handler = new StreamingHandler(stream);
            _httpClient = new HttpClient(_handler, disposeHandler: false);
            Api = new MihomoApiClient(
                _httpClient,
                new Uri("http://127.0.0.1:9090/"),
                "test-secret",
                streamingFirstRecordTimeout,
                maxStreamingRecordBytes);
        }

        public MihomoApiClient Api { get; }

        public void Dispose()
        {
            _httpClient.Dispose();
            _handler.Dispose();
        }
    }

    private sealed class StreamingHandler : HttpMessageHandler
    {
        private readonly Stream _stream;

        public StreamingHandler(Stream stream) => _stream = stream;

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _stream.Dispose();
            }

            base.Dispose(disposing);
        }

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

            int length = Math.Min(Math.Min(count, _chunkSize), _data.Length - _position);
            Buffer.BlockCopy(_data, _position, buffer, offset, length);
            _position += length;
            return length;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            byte[] temporary = new byte[Math.Min(buffer.Length, _chunkSize)];
            int count = Read(temporary, 0, temporary.Length);
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

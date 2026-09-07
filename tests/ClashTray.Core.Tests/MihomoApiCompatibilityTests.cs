using System.Net;
using System.Net.Http;
using System.Text.Json;
using ClashTray.Core;

namespace ClashTray.Core.Tests;

[TestClass]
public sealed class MihomoApiCompatibilityTests
{
    [TestMethod]
    public async Task FakeIpCacheFlushUsesPostAndBearerAuthentication()
    {
        var handler = new RecordingHandler();
        using var httpClient = new HttpClient(handler);
        var api = new MihomoApiClient(httpClient, new Uri("http://127.0.0.1:9090/"), "test-secret");

        using var response = await api.ClearFakeIpCacheAsync();

        Assert.AreEqual(HttpMethod.Post, handler.Method);
        Assert.AreEqual("/cache/fakeip/flush", handler.PathAndQuery);
        Assert.AreEqual("Bearer test-secret", handler.Authorization);
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
}

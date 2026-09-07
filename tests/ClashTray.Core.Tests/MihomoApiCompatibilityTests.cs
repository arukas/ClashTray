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

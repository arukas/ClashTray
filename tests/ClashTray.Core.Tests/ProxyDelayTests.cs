using System.Net;
using System.Text;
using System.Text.Json;
using ClashTray.Core;

namespace ClashTray.Core.Tests;

[TestClass]
public sealed class ProxyDelayTests
{
    [TestMethod]
    public void HistoryUsesLatestRecordAndPreservesUntestedAndFailedStates()
    {
        using var document = JsonDocument.Parse("""
        {"proxies":{
          "Fresh":{"type":"Direct","history":[{"delay":91},{"delay":23}]},
          "Failed":{"type":"Direct","history":[{"delay":27},{"delay":0}]},
          "Empty":{"type":"Direct","history":[]},
          "Missing":{"type":"Direct"},
          "Malformed":{"type":"Direct","history":[{"delay":"fast"}]},
          "Null":{"type":"Direct","history":[null]},
          "Negative":{"type":"Direct","history":[{"delay":-1}]},
          "Automatic":{"type":"URLTest","all":["Fresh","Failed"],"now":"Fresh","history":[{"delay":23}]},
          "Invalid":false
        }}
        """);
        var parsed = MihomoDataParser.ParseProxies(document);
        Assert.AreEqual("23", parsed.Nodes.Single(node => node.Name == "Fresh").Delay);
        Assert.AreEqual("0", parsed.Nodes.Single(node => node.Name == "Failed").Delay);
        Assert.IsTrue(parsed.Nodes.Where(node => node.Name is not ("Fresh" or "Failed")).All(node => node.Delay is null));
        Assert.AreEqual("23", parsed.Groups.Single().Delay);
        Assert.AreEqual("Fresh", parsed.Groups.Single().Current);
    }

    [TestMethod]
    public async Task WholeGroupRequestRefreshesAllMembersAndAutomaticSelection()
    {
        using var handler = new GroupDelayHandler();
        using var client = new HttpClient(handler);
        var api = new MihomoApiClient(client, new Uri("http://127.0.0.1:9090/"), "");
        var root = Path.Combine(Path.GetTempPath(), "ClashTrayTests", Guid.NewGuid().ToString("N"));
        await using var runtime = new ClashTrayRuntime(new AppPaths(Path.Combine(root, "user"), Path.Combine(root, "service")));
        try
        {
            runtime.AttachControllerForTesting(api, false);
            var result = await runtime.TestProxyGroupDelayAsync("自动 / A+#");
            Assert.AreEqual(3, result.Count);
            Assert.AreEqual(31, result["A"]);
            Assert.AreEqual(0, result["B"]);
            Assert.AreEqual("31", runtime.Snapshot.ProxyNodes.Single(node => node.Name == "A").Delay);
            Assert.AreEqual("0", runtime.Snapshot.ProxyNodes.Single(node => node.Name == "B").Delay);
            Assert.AreEqual("55", runtime.Snapshot.ProxyGroups.Single(group => group.Name == "Nested").Delay);
            Assert.AreEqual("A", runtime.Snapshot.ProxyGroups.Single(group => group.Name == "自动 / A+#").Current);
            Assert.AreEqual(1, handler.BatchRequests);
            Assert.AreEqual(1, handler.ProxyRefreshes);
            Assert.IsTrue(handler.LastBatchUri!.AbsolutePath.Contains(Uri.EscapeDataString("自动 / A+#"), StringComparison.Ordinal));
            StringAssert.Contains(handler.LastBatchUri.Query, "timeout=5000");
            StringAssert.Contains(Uri.UnescapeDataString(handler.LastBatchUri.Query), "url=https://www.gstatic.com/generate_204");
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task FailedOrCancelledBatchDoesNotPublishInventedResultsAndCanRetry()
    {
        using var handler = new GroupDelayHandler();
        using var client = new HttpClient(handler);
        var root = Path.Combine(Path.GetTempPath(), "ClashTrayTests", Guid.NewGuid().ToString("N"));
        await using var runtime = new ClashTrayRuntime(new AppPaths(Path.Combine(root, "user"), Path.Combine(root, "service")));
        try
        {
            runtime.AttachControllerForTesting(new MihomoApiClient(client, new Uri("http://127.0.0.1:9090/"), ""), false);
            await runtime.TestProxyGroupDelayAsync("Automatic");
            var before = runtime.Snapshot;
            handler.FailBatch = true;
            await Assert.ThrowsExactlyAsync<HttpRequestException>(() => runtime.TestProxyGroupDelayAsync("Automatic"));
            Assert.AreSame(before, runtime.Snapshot);
            handler.FailBatch = false;
            handler.WaitForCancellation = true;
            using (var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100)))
                await Assert.ThrowsAsync<OperationCanceledException>(() => runtime.TestProxyGroupDelayAsync("Automatic", cancellation.Token));
            Assert.AreSame(before, runtime.Snapshot);
            handler.WaitForCancellation = false;
            Assert.AreEqual(3, (await runtime.TestProxyGroupDelayAsync("Automatic")).Count);
        }
        finally { Directory.Delete(root, true); }
    }

    private sealed class GroupDelayHandler : HttpMessageHandler
    {
        public int BatchRequests { get; private set; }
        public int ProxyRefreshes { get; private set; }
        public Uri? LastBatchUri { get; private set; }
        public bool FailBatch { get; set; }
        public bool WaitForCancellation { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.AreEqual(HttpMethod.Get, request.Method);
            if (request.RequestUri!.AbsolutePath.StartsWith("/group/", StringComparison.Ordinal))
            {
                BatchRequests++;
                LastBatchUri = request.RequestUri;
                if (WaitForCancellation) await Task.Delay(Timeout.Infinite, cancellationToken);
                if (FailBatch) return new HttpResponseMessage(HttpStatusCode.GatewayTimeout);
                return Json("""{"A":31,"B":0,"Nested":55}""");
            }
            Assert.AreEqual("/proxies", request.RequestUri.AbsolutePath);
            ProxyRefreshes++;
            return Json("""
            {"proxies":{
              "A":{"type":"Direct","history":[]},"B":{"type":"Direct","history":[{"delay":87}]},
              "Nested":{"type":"Fallback","all":["A","B"],"now":"A"},
              "自动 / A+#":{"type":"URLTest","all":["A","B","Nested"],"now":"A"}
            }}
            """);
        }

        private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
        { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }
}

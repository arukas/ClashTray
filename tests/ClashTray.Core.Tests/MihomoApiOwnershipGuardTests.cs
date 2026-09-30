using System.Net;

namespace ClashTray.Core.Tests;

[TestClass]
public sealed class MihomoApiOwnershipGuardTests
{
    [TestMethod]
    public async Task HttpReadAndWriteRequestsAreRejectedBeforeDispatchWhenOwnershipIsLost()
    {
        int dispatched = 0;
        using CountingHandler handler = new(() => Interlocked.Increment(ref dispatched));
        using HttpClient http = new(handler);
        MihomoApiClient api = new(
            http,
            new Uri("http://127.0.0.1:9090/"),
            string.Empty,
            controllerOwnershipValidator: static () => false);

        await Assert.ThrowsExactlyAsync<ManagedCoreOwnershipException>(() => api.GetVersionAsync());
        await Assert.ThrowsExactlyAsync<ManagedCoreOwnershipException>(
            () => api.SetTunAsync(enabled: false));

        Assert.AreEqual(0, dispatched, "An unconfirmed controller must receive no HTTP requests.");
    }

    [TestMethod]
    public async Task WebSocketHandshakeIsRejectedBeforeOpeningSocketWhenOwnershipIsLost()
    {
        using CountingHandler handler = new(static () => { });
        using HttpClient http = new(handler);
        MihomoApiClient api = new(
            http,
            new Uri("http://127.0.0.1:9090/"),
            string.Empty,
            webSocketFactory: static () => throw new AssertFailedException("WebSocket must not be created."),
            controllerOwnershipValidator: static () => false);

        await Assert.ThrowsExactlyAsync<ManagedCoreOwnershipException>(
            () => api.ConnectWebSocketAsync("/logs"));
    }

    private sealed class CountingHandler(Action onSend) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            onSend();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }
}

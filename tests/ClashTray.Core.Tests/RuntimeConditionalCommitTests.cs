using System.Net;
using ClashTray.Contracts;

namespace ClashTray.Core.Tests;

[TestClass]
public sealed class RuntimeConditionalCommitTests
{
    [TestMethod]
    public Task StaleHealthResponseCannotOverwriteFailedState() =>
        VerifyStaleRefreshCannotOverwriteFailureAsync(BlockPoint.Configuration);

    [TestMethod]
    public Task StaleOptionalDataCannotOverwriteFailedCoreStatus() =>
        VerifyStaleRefreshCannotOverwriteFailureAsync(BlockPoint.Traffic);

    private static async Task VerifyStaleRefreshCannotOverwriteFailureAsync(BlockPoint blockPoint)
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "ClashTrayTests",
            Guid.NewGuid().ToString("N"));
        AppPaths paths = new(Path.Combine(root, "local"), Path.Combine(root, "program"));
        using BlockingRefreshHandler handler = new(blockPoint);
        using HttpClient httpClient = new(handler);
        MihomoApiClient api = new(
            httpClient,
            new Uri("http://127.0.0.1:9090/"),
            string.Empty);
        await using ClashTrayRuntime runtime = new(paths);

        try
        {
            runtime.AttachControllerForTesting(api, usingServiceCore: false);
            Task refresh = runtime.RefreshControllerDataForTestingAsync();
            await handler.Blocked.Task.WaitAsync(TimeSpan.FromSeconds(2));

            runtime.SetCoreStateForTesting(CoreState.Failed);
            handler.Release();
            await refresh.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.AreEqual(CoreState.Failed, runtime.Snapshot.Core.State);
            Assert.IsFalse(runtime.IsCoreHealthConfirmedForTesting);
        }
        finally
        {
            handler.Release();
            await runtime.DisposeAsync();
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private enum BlockPoint
    {
        Configuration,
        Traffic
    }

    private sealed class BlockingRefreshHandler : HttpMessageHandler
    {
        private readonly BlockPoint _blockPoint;
        private readonly TaskCompletionSource<bool> _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _blocked;

        public BlockingRefreshHandler(BlockPoint blockPoint)
        {
            _blockPoint = blockPoint;
        }

        public TaskCompletionSource<bool> Blocked { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Release() => _release.TrySetResult(true);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            string path = request.RequestUri?.AbsolutePath ?? string.Empty;
            bool shouldBlock = (_blockPoint == BlockPoint.Configuration && path == "/configs")
                || (_blockPoint == BlockPoint.Traffic && path == "/traffic");
            if (shouldBlock && Interlocked.Exchange(ref _blocked, 1) == 0)
            {
                Blocked.TrySetResult(true);
                await _release.Task.WaitAsync(cancellationToken);
            }

            string body = path switch
            {
                "/version" => "{\"version\":\"v1.19.30\"}",
                "/configs" => "{\"mode\":\"rule\",\"tun\":{\"enable\":false}}",
                "/proxies" => "{\"proxies\":{}}",
                "/traffic" => "{\"upTotal\":11,\"downTotal\":12,\"up\":1,\"down\":2}\n",
                "/memory" => "{\"inuse\":22}\n",
                "/connections" => "{\"connections\":[]}",
                "/rules" => "{\"rules\":[]}",
                "/providers/proxies" => "{\"providers\":{}}",
                "/providers/rules" => "{\"providers\":{}}",
                _ => "{}"
            };
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body)
            };
        }
    }
}

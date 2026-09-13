using System.Net;
using System.Text;
using System.Text.Json;
using ClashTray.Contracts;
using ClashTray.Core;
using ClashTray.Service;

namespace ClashTray.IntegrationTests;

[TestClass]
public sealed class TunLifecycleTests
{
    [TestMethod]
    public async Task ShutdownGuardDoesNotWriteWhenTunIsAlreadyOff()
    {
        using TunControllerHandler handler = new(initialTunEnabled: false);
        using HttpClient httpClient = new(handler);
        MihomoApiClient api = new(httpClient, new Uri("http://127.0.0.1:9090/"), string.Empty);

        TunShutdownResult result = await TunShutdownGuard.EnsureDisabledAsync(
            api,
            CancellationToken.None);

        Assert.IsTrue(result.Succeeded);
        Assert.AreEqual(TunState.Off, result.State);
        Assert.AreEqual(0, handler.PatchCount);
    }

    [TestMethod]
    public async Task ShutdownGuardRequiresConfirmedTunDisable()
    {
        using TunControllerHandler handler = new(initialTunEnabled: true)
        {
            ApplyPatchToState = false
        };
        using HttpClient httpClient = new(handler);
        MihomoApiClient api = new(httpClient, new Uri("http://127.0.0.1:9090/"), string.Empty);

        TunShutdownResult result = await TunShutdownGuard.EnsureDisabledAsync(
            api,
            CancellationToken.None);

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual(TunState.Unknown, result.State);
        Assert.AreEqual(1, handler.PatchCount);
        StringAssert.Contains(handler.LastPatchBody, "\"enable\":false", StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task RuntimeRestartDoesNotStartAfterServiceRefusesUnsafeStop()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "ClashTrayIntegrationTests",
            Guid.NewGuid().ToString("N"));
        AppPaths paths = new(
            Path.Combine(root, "local"),
            Path.Combine(root, "program"));
        RejectingServicePipeClient service = new();
        using EmptyControllerHandler controllerHandler = new();
        using HttpClient httpClient = new(controllerHandler);
        MihomoApiClient api = new(httpClient, new Uri("http://127.0.0.1:9090/"), string.Empty);

        try
        {
            await using (ClashTrayRuntime runtime = new(
                paths,
                startupRegistration: null,
                servicePipeClient: service,
                settingsStore: null,
                systemProxy: new InMemorySystemProxyController()))
            {
                runtime.AttachControllerForTesting(api, usingServiceCore: true);

                InvalidOperationException exception = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
                    () => runtime.RestartCoreAsync());

                StringAssert.Contains(exception.Message, "TUN", StringComparison.Ordinal);
                Assert.AreEqual(CoreState.Running, runtime.Snapshot.Core.State);
                Assert.AreEqual(TunState.Unknown, runtime.Snapshot.Tun);
                Assert.AreEqual(1, service.Commands.Count(command => command == ServiceCommand.StopCore));
                Assert.AreEqual(0, service.Commands.Count(command => command == ServiceCommand.StartCore));
            }
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private sealed class TunControllerHandler : HttpMessageHandler
    {
        private bool _tunEnabled;

        public TunControllerHandler(bool initialTunEnabled)
        {
            _tunEnabled = initialTunEnabled;
        }

        public bool ApplyPatchToState { get; init; } = true;

        public int PatchCount { get; private set; }

        public string LastPatchBody { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (request.RequestUri?.AbsolutePath == "/configs"
                && request.Method == HttpMethod.Get)
            {
                return Json($"{{\"tun\":{{\"enable\":{JsonSerializer.Serialize(_tunEnabled)}}}}}");
            }

            if (request.RequestUri?.AbsolutePath == "/configs"
                && request.Method == HttpMethod.Patch)
            {
                PatchCount++;
                LastPatchBody = request.Content is null
                    ? string.Empty
                    : await request.Content.ReadAsStringAsync(cancellationToken);
                if (ApplyPatchToState)
                {
                    _tunEnabled = false;
                }

                return Json("{}");
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage Json(string content) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(content, Encoding.UTF8, "application/json")
        };
    }

    private sealed class EmptyControllerHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json")
            });
    }

    private sealed class RejectingServicePipeClient : IServicePipeClient
    {
        public List<ServiceCommand> Commands { get; } = [];

        public Task<ServiceResponse> SendAsync(
            ServiceCommand command,
            string? payload = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Commands.Add(command);
            if (command == ServiceCommand.StopCore)
            {
                return Task.FromResult(new ServiceResponse(
                    Guid.NewGuid(),
                    false,
                    TunState.Unknown,
                    Error: "停止核心前无法确认 TUN 已关闭，核心保持运行。",
                    Core: CoreState.Running));
            }

            return Task.FromResult(new ServiceResponse(
                Guid.NewGuid(),
                true,
                TunState.Off,
                Core: command == ServiceCommand.StartCore
                    ? CoreState.Running
                    : CoreState.Stopped));
        }
    }

    private sealed class InMemorySystemProxyController : ISystemProxyController
    {
        public SystemProxyState State { get; private set; } = SystemProxyState.Off;

        public SystemProxyState DetectState() => State;

        public Task EnableAsync(
            int port,
            string bypassList,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            State = SystemProxyState.On;
            return Task.CompletedTask;
        }

        public Task DisableAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            State = SystemProxyState.Off;
            return Task.CompletedTask;
        }
    }
}

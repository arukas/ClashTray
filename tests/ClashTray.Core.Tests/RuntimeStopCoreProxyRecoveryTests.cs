using ClashTray.Contracts;

namespace ClashTray.Core.Tests;

[TestClass]
public sealed class RuntimeStopCoreProxyRecoveryTests
{
    [TestMethod]
    public async Task StopCoreAsyncCallerCancellationStillRevokesOwnedSystemProxy()
    {
        string root = Path.Combine(Path.GetTempPath(), "ClashTrayTests", Guid.NewGuid().ToString("N"));
        AppPaths paths = new(Path.Combine(root, "local"), Path.Combine(root, "program"));
        AppSettings settings = RuntimeTestHelpers.CreatePortSafeSettings() with { SystemProxyEnabled = true };
        using CancellationTokenSource stopCancellation = new();
        CancellingStopServiceClient service = new(stopCancellation);
        TokenStrictSystemProxyController proxy = new(SystemProxyState.Off);
        using RuntimeControllerHandler handler = new();
        using HttpClient httpClient = new(handler);
        MihomoApiClient api = new(httpClient, new Uri($"http://127.0.0.1:{settings.ControllerPort}/"), string.Empty);
        await using ClashTrayRuntime runtime = RuntimeTestBuilder.Create(
            paths,
            null,
            service,
            new TestSettingsStore(settings),
            proxy,
            ownership: new FixtureCoreOwnershipObservationSource(), controllerApiFactory: () => api);

        try
        {
            runtime.AttachControllerForTesting(api, usingServiceCore: true);
            await runtime.SetSystemProxyAsync(true);

            Assert.AreEqual(SystemProxyState.On, proxy.State);
            Assert.AreEqual(SystemProxyState.On, runtime.Snapshot.SystemProxy);

            await runtime.StopCoreAsync(stopCancellation.Token);

            Assert.IsTrue(stopCancellation.IsCancellationRequested, "The fixture must cancel the caller token during StopCore to model a cancelled stop request.");
            Assert.AreEqual(1, service.StopCount);
            Assert.AreEqual(CoreState.Stopped, runtime.Snapshot.Core.State);
            Assert.AreEqual(1, proxy.DisableCount, "Core-loss proxy revocation must run even though the caller token was cancelled.");
            Assert.IsFalse(proxy.DisableObservedCancellation, "Disable must be invoked with a live cleanup token instead of the cancelled caller token.");
            Assert.AreEqual(SystemProxyState.Off, proxy.State);
            Assert.AreEqual(SystemProxyState.Off, runtime.Snapshot.SystemProxy);
            Assert.IsNull(runtime.Snapshot.ErrorMessage, "A completed revocation must not leave the recovery-required error message behind.");
            Assert.IsTrue(runtime.Settings.SystemProxyEnabled, "Revoking the proxy on core loss must preserve the saved preference.");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private sealed class CancellingStopServiceClient(CancellationTokenSource stopCancellation) : IServicePipeClient
    {
        public int StopCount { get; private set; }

        public async Task<ServiceResponse> SendAsync(
            ServiceCommand command,
            string? payload = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (command == ServiceCommand.StopCore)
            {
                StopCount++;
                await stopCancellation.CancelAsync();
            }

            return new ServiceResponse(
                Guid.NewGuid(),
                true,
                TunState.Off,
                Core: command == ServiceCommand.StopCore ? CoreState.Stopped : CoreState.Running,
                ProtocolVersion: ServiceProtocol.CurrentVersion,
                RuntimeBinding: null);
        }
    }

    private sealed class TokenStrictSystemProxyController : ISystemProxyController
    {
        public TokenStrictSystemProxyController(SystemProxyState initialState)
        {
            State = initialState;
        }

        public SystemProxyState State { get; private set; }

        public int DisableCount { get; private set; }

        public bool DisableObservedCancellation { get; private set; }

        public SystemProxyState DetectState() => State;

        public Task EnableAsync(int port, string bypassList, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            State = SystemProxyState.On;
            return Task.CompletedTask;
        }

        public Task DisableAsync(CancellationToken cancellationToken = default)
        {
            DisableCount++;
            DisableObservedCancellation |= cancellationToken.IsCancellationRequested;
            cancellationToken.ThrowIfCancellationRequested();
            State = SystemProxyState.Off;
            return Task.CompletedTask;
        }
    }
}

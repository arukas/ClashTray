using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using ClashTray.Contracts;

namespace ClashTray.Core.Tests;

[TestClass]
public sealed class RuntimeOneTimeControllerPortTests
{
    [TestMethod]
    public async Task OneTimeStartReportsSuccessOnlyForItsConfirmedBindingAndDoesNotSaveSettings()
    {
        await using OneTimeStartContext context = await OneTimeStartContext.CreateAsync(includeCore: true);
        await context.ImportConfigurationAsync("mixed-port: 7890\nproxies: []\nproxy-groups: []\nrules: []\n");
        int settingsSaveCountBeforeStart = context.Settings.SaveCount;
        int preferredPortBeforeStart = context.Settings.Settings.ControllerPort;
        int confirmedPort = context.Settings.Settings.ControllerPort + 17;
        context.Service.StartBehavior = (payload, _) => Task.FromResult(Success(payload, context.Settings.Settings, confirmedPort));

        CoreStartOperationResult result = await context.Runtime.StartCoreUsingAvailableControllerPortOnceAsync();

        Assert.IsTrue(result.Succeeded);
        Assert.AreEqual(CoreStartOutcome.Started, result.Outcome);
        Assert.AreNotEqual(Guid.Empty, result.OperationId);
        Assert.AreEqual(confirmedPort, result.ConfirmedControllerPort);
        Assert.IsNotNull(context.Service.LastStartPayload);
        Assert.IsTrue(context.Service.LastStartPayload.UseAvailableControllerPortOnce);
        Assert.AreEqual(preferredPortBeforeStart, context.Service.LastStartPayload.ControllerPort);
        Assert.AreEqual(preferredPortBeforeStart, context.Settings.Settings.ControllerPort);
        Assert.AreEqual(settingsSaveCountBeforeStart, context.Settings.SaveCount, "The one-time override must not alter persisted preferences.");
        Assert.AreEqual(CoreState.Running, context.Runtime.Snapshot.Core.State);
        Assert.IsTrue(context.Runtime.IsCoreHealthConfirmedForTesting);
    }

    [TestMethod]
    public async Task OneTimeStartDistinguishesMissingCoreAndMissingConfiguration()
    {
        await using (OneTimeStartContext missingCore = await OneTimeStartContext.CreateAsync(includeCore: false))
        {
            CoreStartOperationResult result = await missingCore.Runtime.StartCoreUsingAvailableControllerPortOnceAsync();
            Assert.AreEqual(CoreStartOutcome.CoreMissing, result.Outcome);
            Assert.IsFalse(result.Succeeded);
        }

        await using OneTimeStartContext missingConfiguration = await OneTimeStartContext.CreateAsync(includeCore: true);
        CoreStartOperationResult missingConfig = await missingConfiguration.Runtime.StartCoreUsingAvailableControllerPortOnceAsync();
        Assert.AreEqual(CoreStartOutcome.ConfigurationMissing, missingConfig.Outcome);
        Assert.IsFalse(missingConfig.Succeeded);
        Assert.IsFalse(missingConfiguration.Service.Commands.Contains(ServiceCommand.StartCore));
    }

    [TestMethod]
    public async Task OneTimeStartReportsInvalidYamlProxyConflictAndCandidateExhaustion()
    {
        await using (OneTimeStartContext invalidYaml = await OneTimeStartContext.CreateAsync(includeCore: true))
        {
            await invalidYaml.ImportConfigurationAsync("dns: [unterminated\nproxies: []\n");
            invalidYaml.Service.StartBehavior = (payload, _) => Task.FromResult(Failure(
                "Mihomo 配置验证失败。",
                ServiceErrorCode.CoreReadinessFailed));
            CoreStartOperationResult result = await invalidYaml.Runtime.StartCoreUsingAvailableControllerPortOnceAsync();
            Assert.AreEqual(CoreStartOutcome.InvalidConfiguration, result.Outcome);
            Assert.IsFalse(result.Succeeded);
        }

        await using (OneTimeStartContext portConflict = await OneTimeStartContext.CreateAsync(includeCore: true))
        {
            await portConflict.ImportConfigurationAsync("mixed-port: 7890\nproxies: []\n");
            portConflict.Service.StartBehavior = (_, _) => Task.FromResult(Failure(
                "Mixed 监听端口已被占用。",
                ServiceErrorCode.ProxyPortConflict));
            CoreStartOperationResult result = await portConflict.Runtime.StartCoreUsingAvailableControllerPortOnceAsync();
            Assert.AreEqual(CoreStartOutcome.PortConflict, result.Outcome);
            Assert.IsFalse(result.Succeeded);
        }

        await using OneTimeStartContext exhausted = await OneTimeStartContext.CreateAsync(includeCore: true);
        await exhausted.ImportConfigurationAsync("mixed-port: 7890\nproxies: []\n");
        exhausted.Service.StartBehavior = (_, _) => Task.FromResult(Failure(
            "没有找到可用的高位控制器端口（最多探测 16 个候选）。",
            ServiceErrorCode.ControllerCandidatesExhausted));
        CoreStartOperationResult candidates = await exhausted.Runtime.StartCoreUsingAvailableControllerPortOnceAsync();
        Assert.AreEqual(CoreStartOutcome.ControllerCandidatesExhausted, candidates.Outcome);
        Assert.IsFalse(candidates.Succeeded);
    }

    [TestMethod]
    public async Task OneTimeStartTimeoutAndCancellationReturnTypedNonSuccessResults()
    {
        await using (OneTimeStartContext timedOut = await OneTimeStartContext.CreateAsync(
            includeCore: true,
            startupBudget: TimeSpan.FromSeconds(2)))
        {
            await timedOut.ImportConfigurationAsync("mixed-port: 7890\nproxies: []\n");
            timedOut.Service.StartBehavior = async (_, cancellationToken) =>
            {
                timedOut.Service.StartEntered.TrySetResult(true);
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return Failure("unreachable", ServiceErrorCode.CoreReadinessFailed);
            };

            Task<CoreStartOperationResult> operation = timedOut.Runtime.StartCoreUsingAvailableControllerPortOnceAsync();
            await timedOut.Service.StartEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            CoreStartOperationResult result = await operation.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.AreEqual(CoreStartOutcome.TimedOut, result.Outcome);
            Assert.IsFalse(result.Succeeded);
        }

        await using OneTimeStartContext cancelled = await OneTimeStartContext.CreateAsync(includeCore: true);
        await cancelled.ImportConfigurationAsync("mixed-port: 7890\nproxies: []\n");
        cancelled.Service.StartBehavior = async (_, cancellationToken) =>
        {
            cancelled.Service.StartEntered.TrySetResult(true);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return Failure("unreachable", ServiceErrorCode.CoreReadinessFailed);
        };
        using CancellationTokenSource cancellation = new();
        Task<CoreStartOperationResult> cancelledOperation = cancelled.Runtime.StartCoreUsingAvailableControllerPortOnceAsync(
            cancellation.Token);
        await cancelled.Service.StartEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await cancellation.CancelAsync();
        CoreStartOperationResult cancelledResult = await cancelledOperation.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(CoreStartOutcome.Cancelled, cancelledResult.Outcome);
        Assert.IsFalse(cancelledResult.Succeeded);
        Assert.AreEqual(CoreState.Failed, cancelled.Runtime.Snapshot.Core.State);
    }

    private static ServiceResponse Success(ServiceCorePayload payload, AppSettings settings, int actualControllerPort)
    {
        CoreRuntimeBinding binding = RuntimeTestHelpers.CreateRuntimeBinding(settings) with
        {
            PreferredControllerPort = payload.ControllerPort,
            ControllerPort = actualControllerPort
        };
        return new ServiceResponse(
            Guid.NewGuid(),
            true,
            TunState.Off,
            Core: CoreState.Running,
            ProtocolVersion: ServiceProtocol.CurrentVersion,
            RuntimeBinding: binding);
    }

    private static ServiceResponse Failure(string message, ServiceErrorCode errorCode) => new(
        Guid.NewGuid(),
        false,
        TunState.Off,
        Error: message,
        Core: CoreState.Failed,
        ErrorCode: errorCode,
        ProtocolVersion: ServiceProtocol.CurrentVersion);

    private sealed class OneTimeStartContext : IAsyncDisposable
    {
        private readonly string _root;
        private readonly HttpClient _httpClient;

        private OneTimeStartContext(
            string root,
            TestSettingsStore settings,
            OneTimeStartService service,
            HttpClient httpClient,
            ClashTrayRuntime runtime)
        {
            _root = root;
            Settings = settings;
            Service = service;
            _httpClient = httpClient;
            Runtime = runtime;
        }

        public TestSettingsStore Settings { get; }

        public OneTimeStartService Service { get; }

        public ClashTrayRuntime Runtime { get; }

        public static async Task<OneTimeStartContext> CreateAsync(
            bool includeCore,
            TimeSpan? startupBudget = null)
        {
            string root = Path.Combine(Path.GetTempPath(), "ClashTrayTests", Guid.NewGuid().ToString("N"));
            AppPaths paths = new(Path.Combine(root, "local"), Path.Combine(root, "program"));
            TestSettingsStore settings = new(RuntimeTestHelpers.CreatePortSafeSettings());
            OneTimeStartService service = new(settings.Settings);
            HttpClient httpClient = CreateHttpClient();
            MihomoApiClient api = new(
                httpClient,
                new Uri($"http://127.0.0.1:{settings.Settings.ControllerPort}/"),
                string.Empty);
            if (includeCore)
            {
                await PrepareManagedCoreAsync(paths);
            }

            ClashTrayRuntime runtime = new(
                paths,
                null,
                service,
                settings,
                candidateValidator: new AcceptingCandidateValidator(),
                controllerApiFactory: () => api,
                coreStartupBudget: startupBudget);
            await runtime.InitializeAsync();
            return new OneTimeStartContext(root, settings, service, httpClient, runtime);
        }

        public async Task ImportConfigurationAsync(string yaml)
        {
            string source = Path.Combine(_root, "source.yaml");
            await File.WriteAllTextAsync(source, yaml);
            await Runtime.ImportLocalConfigurationAsync(source, "one-time-start-test");
        }

        public async ValueTask DisposeAsync()
        {
            await Runtime.DisposeAsync();
            _httpClient.Dispose();
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage(
            "Reliability",
            "CA2000:Dispose objects before losing scope",
            Justification = "The HttpClient stored by the test context owns and disposes its message handler in DisposeAsync.")]
        private static HttpClient CreateHttpClient() => new(new OneTimeControllerHandler());

        private static async Task PrepareManagedCoreAsync(AppPaths paths)
        {
            paths.EnsureDirectories();
            Directory.CreateDirectory(paths.CoreRoot);
            File.Copy(Environment.ProcessPath!, paths.ManagedCoreExecutable);
            ManagedCoreMetadata metadata = new(
                "v0.0.0-test",
                new Uri("https://example.test/mihomo.zip"),
                new string('0', 64),
                new string('0', 64));
            await File.WriteAllTextAsync(paths.ManagedCoreMetadata, JsonSerializer.Serialize(metadata));
        }
    }

    private sealed class OneTimeStartService(AppSettings settings) : IServicePipeClient
    {
        private static readonly JsonSerializerOptions WebJsonOptions = new(JsonSerializerDefaults.Web);

        public ConcurrentQueue<ServiceCommand> Commands { get; } = new();

        public ServiceCorePayload? LastStartPayload { get; private set; }

        public TaskCompletionSource<bool> StartEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Func<ServiceCorePayload, CancellationToken, Task<ServiceResponse>> StartBehavior { get; set; } =
            (payload, _) => Task.FromResult(Success(payload, settings, payload.ControllerPort));

        public async Task<ServiceResponse> SendAsync(
            ServiceCommand command,
            string? payload = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Commands.Enqueue(command);
            if (command == ServiceCommand.GetStatus)
            {
                return new ServiceResponse(
                    Guid.NewGuid(),
                    true,
                    TunState.Off,
                    Core: CoreState.Stopped,
                    ProtocolVersion: ServiceProtocol.CurrentVersion);
            }

            if (command == ServiceCommand.StartCore)
            {
                ServiceCorePayload startPayload = JsonSerializer.Deserialize<ServiceCorePayload>(
                    payload ?? throw new InvalidDataException("Missing service payload."),
                    WebJsonOptions)
                    ?? throw new InvalidDataException("Invalid service payload.");
                LastStartPayload = startPayload;
                StartEntered.TrySetResult(true);
                return await StartBehavior(startPayload, cancellationToken);
            }

            return new ServiceResponse(
                Guid.NewGuid(),
                true,
                TunState.Off,
                Core: CoreState.Stopped,
                ProtocolVersion: ServiceProtocol.CurrentVersion);
        }
    }

    private sealed class OneTimeControllerHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string body = request.RequestUri?.AbsolutePath switch
            {
                "/version" => "{\"version\":\"v1.19.31\"}",
                "/configs" => "{\"mode\":\"rule\",\"tun\":{\"enable\":false}}",
                "/proxies" => "{\"proxies\":{}}",
                "/traffic" => "{\"upTotal\":0,\"downTotal\":0,\"up\":0,\"down\":0}",
                "/memory" => "{\"inuse\":0}",
                "/connections" => "{\"connections\":[]}",
                "/rules" => "{\"rules\":[]}",
                "/providers/proxies" => "{\"providers\":{}}",
                "/providers/rules" => "{\"providers\":{}}",
                _ => "{}"
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body)
            });
        }
    }
}
